using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.Hyprland;

internal sealed class HyprlandService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconcileDelay = TimeSpan.FromMilliseconds(75);

    [Flags]
    private enum ReconcileKind
    {
        None = 0,
        ActiveWindow = 1,
        Clients = 2,
        Topology = 4,
        Full = 8,
    }

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Lock _reconcileGate = new();
    private readonly Task _eventTask;
    private readonly Task _initialRefreshTask;
    private readonly string? _requestSocketPath;
    private readonly string? _eventSocketPath;
    private volatile HyprlandSnapshot _snapshot = HyprlandSnapshot.Empty;
    private Task _reconcileTask = Task.CompletedTask;
    private ReconcileKind _pendingReconciliation;
    private string _focusedAddress = "";
    private bool _reconcileScheduled;
    private bool _disposed;

    public HyprlandSnapshot Snapshot => _snapshot;

    public HyprlandService()
    {
        (_requestSocketPath, _eventSocketPath) = ResolveSocketPaths();
        _eventTask = RunEventLoopAsync(_cts.Token);
        _initialRefreshTask = RefreshAsync(_cts.Token);
    }

    public void Dispose()
    {
        lock (_reconcileGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _cts.Cancel();
        Task reconcileTask;
        lock (_reconcileGate)
        {
            reconcileTask = _reconcileTask;
        }

        try
        {
            if (!Task.WhenAll(_eventTask, _initialRefreshTask, reconcileTask)
                    .Wait(TimeSpan.FromSeconds(2)))
            {
                AppLogger.Warning("Hyprland", "IPC tasks did not stop before the shutdown timeout");
                return;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Hyprland", "IPC tasks did not stop cleanly", exception);
        }

        _refreshLock.Dispose();
        _cts.Dispose();
    }

    private async Task RunEventLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (string.IsNullOrWhiteSpace(_eventSocketPath) || !File.Exists(_eventSocketPath))
            {
                await DelayReconnect(cancellationToken);
                continue;
            }

            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_eventSocketPath), cancellationToken);

                await using var stream = new NetworkStream(socket, ownsSocket: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                await RefreshAsync(cancellationToken);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    await HandleEventAsync(line, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Hyprland", "Event socket disconnected", exception);
                await DelayReconnect(cancellationToken);
            }
        }
    }

    private async Task HandleEventAsync(string line, CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            var separator = line.IndexOf(">>", StringComparison.Ordinal);
            if (separator <= 0)
            {
                return;
            }

            var name = line[..separator];
            var data = line[(separator + 2)..];
            var current = _snapshot;
            if (!current.Available && name != "activelayout")
            {
                ScheduleReconciliation(ReconcileKind.Full);
                return;
            }

            switch (name)
            {
                case "activelayout":
                    UpdateLayoutFromEvent(data);
                    break;
                case "activewindow":
                    UpdateActiveWindowFromEvent(data);
                    break;
                case "activewindowv2":
                    if (!UpdateActiveWindowByAddress(data))
                    {
                        ScheduleReconciliation(ReconcileKind.ActiveWindow | ReconcileKind.Clients);
                    }
                    break;
                case "workspace":
                case "workspacev2":
                    if (!TryGetWorkspaceId(data, name.EndsWith("v2", StringComparison.Ordinal), out var workspaceId) ||
                        !UpdateFocusedWorkspace(workspaceId, null))
                    {
                        ScheduleReconciliation(ReconcileKind.Topology);
                    }
                    break;
                case "focusedmon":
                case "focusedmonv2":
                    if (!TryGetFocusedMonitor(data, out var monitorName, out workspaceId) ||
                        !UpdateFocusedWorkspace(workspaceId, monitorName))
                    {
                        ScheduleReconciliation(ReconcileKind.Topology);
                    }
                    break;
                case "closewindow":
                    RemoveWindow(data.Trim());
                    ScheduleReconciliation(ReconcileKind.ActiveWindow);
                    break;
                case "windowtitlev2":
                    if (!UpdateWindowTitle(data))
                    {
                        ScheduleReconciliation(ReconcileKind.Clients);
                    }
                    break;
                case "windowtitle":
                    ScheduleReconciliation(ReconcileKind.Clients | ReconcileKind.ActiveWindow);
                    break;
                case "movewindow":
                case "movewindowv2":
                    if (!TryMoveWindow(data, name.EndsWith("v2", StringComparison.Ordinal)))
                    {
                        ScheduleReconciliation(ReconcileKind.Clients | ReconcileKind.Topology);
                    }
                    break;
                case "openwindow":
                    ScheduleReconciliation(ReconcileKind.Clients);
                    break;
                case "createworkspace":
                case "createworkspacev2":
                case "destroyworkspace":
                case "destroyworkspacev2":
                case "moveworkspace":
                case "moveworkspacev2":
                case "renameworkspace":
                    ScheduleReconciliation(ReconcileKind.Topology);
                    break;
                case "monitorremoved":
                case "monitorremovedv2":
                case "monitoradded":
                case "monitoraddedv2":
                    ScheduleReconciliation(ReconcileKind.Full);
                    break;
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var activeJson = await RequestJsonAsync("activewindow", timeout.Token);
            var clientsJson = await RequestJsonAsync("clients", timeout.Token);
            var workspacesJson = await RequestJsonAsync("workspaces", timeout.Token);
            var monitorsJson = await RequestJsonAsync("monitors", timeout.Token);
            var devicesJson = await RequestJsonAsync("devices", timeout.Token);

            var active = Deserialize(activeJson, HyprlandJsonContext.Default.HyprClient);
            var clients = DeserializeArray(clientsJson, HyprlandJsonContext.Default.HyprClientArray);
            var workspaces = DeserializeArray(workspacesJson, HyprlandJsonContext.Default.HyprWorkspaceArray);
            var monitors = DeserializeArray(monitorsJson, HyprlandJsonContext.Default.HyprMonitorArray);
            var devices = Deserialize(devicesJson, HyprlandJsonContext.Default.HyprDevices);

            _focusedAddress = active?.Address ?? "";
            _snapshot = BuildSnapshot(active, clients, workspaces, monitors, devices);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (!_snapshot.Available)
        {
            AppLogger.Warning("Hyprland", "Could not refresh compositor state", exception);
            _snapshot = BuildUnavailableSnapshot();
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Hyprland", "Could not refresh compositor state; keeping the previous snapshot", exception);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private void ScheduleReconciliation(ReconcileKind kind)
    {
        lock (_reconcileGate)
        {
            if (_disposed)
            {
                return;
            }

            _pendingReconciliation |= kind;
            if (_reconcileScheduled)
            {
                return;
            }

            _reconcileScheduled = true;
            _reconcileTask = RunReconciliationAsync(_cts.Token);
        }
    }

    private async Task RunReconciliationAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(ReconcileDelay, cancellationToken);

                ReconcileKind kind;
                lock (_reconcileGate)
                {
                    kind = _pendingReconciliation;
                    _pendingReconciliation = ReconcileKind.None;
                }

                if ((kind & ReconcileKind.Full) != 0)
                {
                    await RefreshAsync(cancellationToken);
                }
                else
                {
                    await ReconcileAsync(kind, cancellationToken);
                }

                lock (_reconcileGate)
                {
                    if (_pendingReconciliation == ReconcileKind.None)
                    {
                        _reconcileScheduled = false;
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_reconcileGate)
            {
                _reconcileScheduled = false;
            }
        }
    }

    private async Task ReconcileAsync(ReconcileKind kind, CancellationToken cancellationToken)
    {
        if (kind == ReconcileKind.None)
        {
            return;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            HyprClient? active = null;
            HyprClient[]? clients = null;
            HyprWorkspace[]? workspaces = null;
            HyprMonitor[]? monitors = null;

            if ((kind & ReconcileKind.ActiveWindow) != 0)
            {
                active = DeserializeRequired(
                    await RequestJsonAsync("activewindow", timeout.Token),
                    HyprlandJsonContext.Default.HyprClient);
            }

            if ((kind & ReconcileKind.Clients) != 0)
            {
                clients = DeserializeArrayRequired(
                    await RequestJsonAsync("clients", timeout.Token),
                    HyprlandJsonContext.Default.HyprClientArray);
            }

            if ((kind & ReconcileKind.Topology) != 0)
            {
                workspaces = DeserializeArrayRequired(
                    await RequestJsonAsync("workspaces", timeout.Token),
                    HyprlandJsonContext.Default.HyprWorkspaceArray);
                monitors = DeserializeArrayRequired(
                    await RequestJsonAsync("monitors", timeout.Token),
                    HyprlandJsonContext.Default.HyprMonitorArray);
            }

            var current = _snapshot;
            if (workspaces is not null && monitors is not null)
            {
                current = ApplyTopology(current, workspaces, monitors);
            }

            if (clients is not null)
            {
                current = ApplyClients(current, clients);
            }

            if ((kind & ReconcileKind.ActiveWindow) != 0)
            {
                _focusedAddress = active?.Address ?? "";
                current = ApplyActiveWindow(current, active);
            }

            _snapshot = current;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Hyprland", "Could not reconcile compositor event state; keeping the previous snapshot", exception);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<string?> RequestJsonAsync(string command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_requestSocketPath) || !File.Exists(_requestSocketPath))
        {
            return null;
        }

        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_requestSocketPath), cancellationToken);

        var request = Encoding.UTF8.GetBytes("j/" + command);
        await socket.SendAsync(request, SocketFlags.None, cancellationToken);

        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var received = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
            if (received == 0)
            {
                break;
            }

            output.Write(buffer, 0, received);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static HyprlandSnapshot BuildSnapshot(
        HyprClient? active,
        IReadOnlyList<HyprClient> clients,
        IReadOnlyList<HyprWorkspace> workspaces,
        IReadOnlyList<HyprMonitor> monitors,
        HyprDevices? devices)
    {
        if (monitors.Count == 0 && workspaces.Count == 0)
        {
            return BuildUnavailableSnapshot();
        }

        var currentMonitor = monitors.FirstOrDefault(monitor => monitor.Focused)
                             ?? monitors.FirstOrDefault();
        var activeWorkspace = currentMonitor?.ActiveWorkspace?.Id
                              ?? workspaces.FirstOrDefault(workspace => workspace.Id > 0)?.Id
                              ?? active?.Workspace?.Id
                              ?? 1;

        var workspacesByMonitor = workspaces
            .Where(workspace => workspace.Id > 0)
            .GroupBy(workspace => workspace.Monitor ?? "")
            .ToDictionary(
                group => group.Key,
                group => group.Select(workspace => workspace.Id).Distinct().Order().ToArray());

        var windows = clients
            .Select(ToWindowSummary)
            .ToArray();

        var clientsByWorkspace = clients
            .Where(client => client.Workspace?.Id > 0)
            .GroupBy(client => client.Workspace!.Id)
            .ToDictionary(
                group => group.Key,
                group => group.Select(ToWindowSummary).ToArray());

        var monitorSnapshots = new List<MonitorWorkspaceSnapshot>();
        foreach (var monitor in monitors.OrderByDescending(monitor => monitor.Name == currentMonitor?.Name)
                     .ThenBy(monitor => monitor.Name))
        {
            var ids = workspacesByMonitor.TryGetValue(monitor.Name, out var monitorIds)
                ? monitorIds.ToList()
                : [];
            var monitorActiveWorkspace = monitor.ActiveWorkspace?.Id ?? 0;
            if (monitorActiveWorkspace > 0 && !ids.Contains(monitorActiveWorkspace))
            {
                ids.Add(monitorActiveWorkspace);
            }

            ids.Sort();
            var workspaceSnapshots = ids.Select(id => BuildWorkspaceSnapshot(
                    id,
                    monitor.Name,
                    id == monitorActiveWorkspace,
                    clientsByWorkspace.TryGetValue(id, out var windows) ? windows : []))
                .ToArray();

            monitorSnapshots.Add(new MonitorWorkspaceSnapshot(
                monitor.Name,
                monitor.Name == currentMonitor?.Name,
                monitorActiveWorkspace,
                workspaceSnapshots));
        }

        var workspaceList = monitorSnapshots
            .SelectMany(monitor => monitor.Workspaces)
            .OrderBy(workspace => workspace.Id)
            .ToArray();
        var keyboard = devices?.Keyboards?.FirstOrDefault(keyboard => keyboard.Main)
                       ?? devices?.Keyboards?.FirstOrDefault();

        return new HyprlandSnapshot(
            workspaceList,
            monitorSnapshots,
            windows,
            FirstNonEmpty(active?.Title, active?.ClassName, "Desktop"),
            active?.ClassName ?? "",
            active?.Workspace?.Id > 0 ? active.Workspace.Id : activeWorkspace,
            keyboard?.Name ?? "",
            keyboard?.ActiveKeymap ?? "",
            true);
    }

    private static WorkspaceSnapshot BuildWorkspaceSnapshot(
        int id,
        string monitorName,
        bool active,
        IReadOnlyList<WindowSummary> windows)
    {
        var popupRows = windows.Count == 0
            ? [new PopupRowSnapshot("(empty)", PopupRowKind.Action, false)]
            : windows.Select(window => new PopupRowSnapshot(window.Title)).ToArray();

        return new WorkspaceSnapshot(
            id,
            monitorName,
            active,
            windows,
            new PopupSnapshot($"workspace-{id}", $"Workspace {id}", popupRows));
    }

    private HyprlandSnapshot ApplyClients(HyprlandSnapshot current, IReadOnlyList<HyprClient> clients)
    {
        var windows = clients.Select(ToWindowSummary).ToArray();
        var clientsByWorkspace = clients
            .Where(client => client.Workspace?.Id > 0)
            .GroupBy(client => client.Workspace!.Id)
            .ToDictionary(group => group.Key, group => group.Select(ToWindowSummary).ToArray());

        var monitors = current.MonitorWorkspaces
            .Select(monitor => monitor with
            {
                Workspaces = monitor.Workspaces
                    .Select(workspace => WithWindows(
                        workspace,
                        clientsByWorkspace.TryGetValue(workspace.Id, out var workspaceWindows)
                            ? workspaceWindows
                            : []))
                    .ToArray(),
            })
            .ToArray();

        var updated = current with
        {
            Windows = windows,
            MonitorWorkspaces = monitors,
            Workspaces = monitors.SelectMany(monitor => monitor.Workspaces).OrderBy(workspace => workspace.Id).ToArray(),
        };
        if (string.IsNullOrWhiteSpace(_focusedAddress))
        {
            return updated;
        }

        var focused = clients.FirstOrDefault(client => AddressEquals(client.Address, _focusedAddress));
        return focused is null ? updated : ApplyActiveWindow(updated, focused);
    }

    private static HyprlandSnapshot ApplyTopology(
        HyprlandSnapshot current,
        IReadOnlyList<HyprWorkspace> workspaces,
        IReadOnlyList<HyprMonitor> monitors)
    {
        if (monitors.Count == 0 && workspaces.Count == 0)
        {
            throw new InvalidDataException("Hyprland returned no monitor or workspace topology.");
        }

        var currentMonitor = monitors.FirstOrDefault(monitor => monitor.Focused)
                             ?? monitors.FirstOrDefault();
        var workspacesByMonitor = workspaces
            .Where(workspace => workspace.Id > 0)
            .GroupBy(workspace => workspace.Monitor ?? "")
            .ToDictionary(
                group => group.Key,
                group => group.Select(workspace => workspace.Id).Distinct().Order().ToArray());
        var windowsByWorkspace = current.MonitorWorkspaces
            .SelectMany(monitor => monitor.Workspaces)
            .GroupBy(workspace => workspace.Id)
            .ToDictionary(group => group.Key, group => group.SelectMany(workspace => workspace.Windows).ToArray());

        var monitorSnapshots = new List<MonitorWorkspaceSnapshot>();
        foreach (var monitor in monitors.OrderByDescending(monitor => monitor.Name == currentMonitor?.Name)
                     .ThenBy(monitor => monitor.Name))
        {
            var ids = workspacesByMonitor.TryGetValue(monitor.Name, out var monitorIds)
                ? monitorIds.ToList()
                : [];
            var activeWorkspaceId = monitor.ActiveWorkspace?.Id ?? 0;
            if (activeWorkspaceId > 0 && !ids.Contains(activeWorkspaceId))
            {
                ids.Add(activeWorkspaceId);
            }

            ids.Sort();
            var workspaceSnapshots = ids.Select(id => BuildWorkspaceSnapshot(
                    id,
                    monitor.Name,
                    id == activeWorkspaceId,
                    windowsByWorkspace.TryGetValue(id, out var windows) ? windows : []))
                .ToArray();
            monitorSnapshots.Add(new MonitorWorkspaceSnapshot(
                monitor.Name,
                monitor.Name == currentMonitor?.Name,
                activeWorkspaceId,
                workspaceSnapshots));
        }

        var focusedWorkspaceId = currentMonitor?.ActiveWorkspace?.Id;
        return current with
        {
            MonitorWorkspaces = monitorSnapshots,
            Workspaces = monitorSnapshots.SelectMany(monitor => monitor.Workspaces)
                .OrderBy(workspace => workspace.Id)
                .ToArray(),
            FocusedWorkspaceId = focusedWorkspaceId > 0 ? focusedWorkspaceId.Value : current.FocusedWorkspaceId,
            Available = true,
        };
    }

    private static HyprlandSnapshot ApplyActiveWindow(HyprlandSnapshot current, HyprClient? active)
    {
        if (active is null)
        {
            return current with
            {
                FocusedTitle = "Desktop",
                FocusedClassName = "",
            };
        }

        return current with
        {
            FocusedTitle = FirstNonEmpty(active.Title, active.ClassName, "Desktop"),
            FocusedClassName = active.ClassName ?? "",
            FocusedWorkspaceId = active.Workspace?.Id > 0
                ? active.Workspace.Id
                : current.FocusedWorkspaceId,
        };
    }

    private static WorkspaceSnapshot WithWindows(WorkspaceSnapshot workspace, IReadOnlyList<WindowSummary> windows) =>
        BuildWorkspaceSnapshot(workspace.Id, workspace.MonitorName, workspace.Active, windows);

    private static WindowSummary ToWindowSummary(HyprClient client) => new(
        client.Address ?? "",
        client.ClassName ?? "",
        client.InitialClassName ?? "",
        FirstNonEmpty(client.Title, client.ClassName, client.InitialClassName, "(untitled)"));

    private void UpdateLayoutFromEvent(string data)
    {
        var parts = data.Split(',', 2, StringSplitOptions.TrimEntries);
        var keyboardName = parts.ElementAtOrDefault(0) ?? "";
        var layoutName = parts.ElementAtOrDefault(1) ?? "";
        var current = _snapshot;

        _snapshot = current with
        {
            KeyboardName = string.IsNullOrWhiteSpace(keyboardName) ? current.KeyboardName : keyboardName,
            LayoutName = layoutName,
        };
    }

    private void UpdateActiveWindowFromEvent(string data)
    {
        var parts = data.Split(',', 2, StringSplitOptions.TrimEntries);
        var className = parts.ElementAtOrDefault(0) ?? "";
        var title = parts.ElementAtOrDefault(1) ?? "";
        _focusedAddress = "";
        _snapshot = _snapshot with
        {
            FocusedTitle = FirstNonEmpty(title, className, "Desktop"),
            FocusedClassName = className,
        };
    }

    private bool UpdateActiveWindowByAddress(string data)
    {
        var address = data.Trim();
        var current = _snapshot;
        var window = current.Windows.FirstOrDefault(window => AddressEquals(window.Address, address));
        if (window is null)
        {
            return false;
        }

        var workspace = current.MonitorWorkspaces
            .SelectMany(monitor => monitor.Workspaces)
            .FirstOrDefault(workspace => workspace.Windows.Any(candidate => AddressEquals(candidate.Address, address)));
        _focusedAddress = address;
        _snapshot = current with
        {
            FocusedTitle = window.Title,
            FocusedClassName = window.ClassName,
            FocusedWorkspaceId = workspace?.Id ?? current.FocusedWorkspaceId,
        };
        return true;
    }

    private bool UpdateFocusedWorkspace(int workspaceId, string? monitorName)
    {
        if (workspaceId <= 0)
        {
            return false;
        }

        var current = _snapshot;
        var selectedMonitor = string.IsNullOrWhiteSpace(monitorName)
            ? current.MonitorWorkspaces.FirstOrDefault(monitor => monitor.Current)
            : current.MonitorWorkspaces.FirstOrDefault(monitor =>
                string.Equals(monitor.Name, monitorName, StringComparison.Ordinal));
        if (selectedMonitor is null)
        {
            return false;
        }

        var monitors = current.MonitorWorkspaces.Select(monitor =>
        {
            var selected = string.Equals(monitor.Name, selectedMonitor.Name, StringComparison.Ordinal);
            var activeWorkspaceId = selected ? workspaceId : monitor.ActiveWorkspaceId;
            var workspaces = monitor.Workspaces.ToList();
            if (selected && workspaces.All(workspace => workspace.Id != workspaceId))
            {
                workspaces.Add(BuildWorkspaceSnapshot(workspaceId, monitor.Name, true, []));
            }

            return monitor with
            {
                Current = monitorName is null ? monitor.Current : selected,
                ActiveWorkspaceId = activeWorkspaceId,
                Workspaces = workspaces
                    .Select(workspace => workspace with { Active = workspace.Id == activeWorkspaceId })
                    .OrderBy(workspace => workspace.Id)
                    .ToArray(),
            };
        }).ToArray();

        _snapshot = current with
        {
            MonitorWorkspaces = monitors,
            Workspaces = monitors.SelectMany(monitor => monitor.Workspaces).OrderBy(workspace => workspace.Id).ToArray(),
            FocusedWorkspaceId = workspaceId,
        };
        return true;
    }

    private void RemoveWindow(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        var current = _snapshot;
        var monitors = current.MonitorWorkspaces.Select(monitor => monitor with
        {
            Workspaces = monitor.Workspaces.Select(workspace => WithWindows(
                    workspace,
                    workspace.Windows.Where(window => !AddressEquals(window.Address, address)).ToArray()))
                .ToArray(),
        }).ToArray();
        _snapshot = current with
        {
            Windows = current.Windows.Where(window => !AddressEquals(window.Address, address)).ToArray(),
            MonitorWorkspaces = monitors,
            Workspaces = monitors.SelectMany(monitor => monitor.Workspaces).OrderBy(workspace => workspace.Id).ToArray(),
        };
    }

    private bool UpdateWindowTitle(string data)
    {
        var parts = data.Split(',', 2, StringSplitOptions.TrimEntries);
        var address = parts.ElementAtOrDefault(0) ?? "";
        var title = parts.ElementAtOrDefault(1) ?? "";
        var current = _snapshot;
        var existing = current.Windows.FirstOrDefault(window => AddressEquals(window.Address, address));
        if (existing is null)
        {
            return false;
        }

        var updatedWindow = existing with { Title = FirstNonEmpty(title, existing.ClassName, "(untitled)") };
        var monitors = current.MonitorWorkspaces.Select(monitor => monitor with
        {
            Workspaces = monitor.Workspaces.Select(workspace => WithWindows(
                    workspace,
                    workspace.Windows.Select(window => AddressEquals(window.Address, address) ? updatedWindow : window).ToArray()))
                .ToArray(),
        }).ToArray();
        _snapshot = current with
        {
            Windows = current.Windows.Select(window => AddressEquals(window.Address, address) ? updatedWindow : window).ToArray(),
            MonitorWorkspaces = monitors,
            Workspaces = monitors.SelectMany(monitor => monitor.Workspaces).OrderBy(workspace => workspace.Id).ToArray(),
            FocusedTitle = AddressEquals(_focusedAddress, address) ? updatedWindow.Title : current.FocusedTitle,
        };
        return true;
    }

    private bool TryMoveWindow(string data, bool version2)
    {
        var parts = data.Split(',', StringSplitOptions.TrimEntries);
        var address = parts.ElementAtOrDefault(0) ?? "";
        var workspacePart = parts.ElementAtOrDefault(1) ?? "";
        if (!int.TryParse(workspacePart, out var workspaceId) || workspaceId <= 0)
        {
            return false;
        }

        var current = _snapshot;
        var window = current.Windows.FirstOrDefault(window => AddressEquals(window.Address, address));
        if (window is null || current.Workspaces.All(workspace => workspace.Id != workspaceId))
        {
            return false;
        }

        var monitors = current.MonitorWorkspaces.Select(monitor => monitor with
        {
            Workspaces = monitor.Workspaces.Select(workspace =>
            {
                var windows = workspace.Windows.Where(candidate => !AddressEquals(candidate.Address, address)).ToList();
                if (workspace.Id == workspaceId)
                {
                    windows.Add(window);
                }

                return WithWindows(workspace, windows);
            }).ToArray(),
        }).ToArray();
        _snapshot = current with
        {
            MonitorWorkspaces = monitors,
            Workspaces = monitors.SelectMany(monitor => monitor.Workspaces).OrderBy(workspace => workspace.Id).ToArray(),
            FocusedWorkspaceId = AddressEquals(_focusedAddress, address) ? workspaceId : current.FocusedWorkspaceId,
        };
        return true;
    }

    private static bool TryGetWorkspaceId(string data, bool version2, out int workspaceId)
    {
        var value = version2 ? data.Split(',', 2, StringSplitOptions.TrimEntries)[0] : data.Trim();
        return int.TryParse(value, out workspaceId) && workspaceId > 0;
    }

    private static bool TryGetFocusedMonitor(string data, out string monitorName, out int workspaceId)
    {
        var parts = data.Split(',', 2, StringSplitOptions.TrimEntries);
        monitorName = parts.ElementAtOrDefault(0) ?? "";
        workspaceId = 0;
        return !string.IsNullOrWhiteSpace(monitorName) &&
               int.TryParse(parts.ElementAtOrDefault(1), out workspaceId) &&
               workspaceId > 0;
    }

    private static bool AddressEquals(string? left, string? right)
    {
        static ReadOnlySpan<char> Normalize(string? value)
        {
            var span = value.AsSpan().Trim();
            return span.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? span[2..] : span;
        }

        return Normalize(left).Equals(Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task DelayReconnect(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ReconnectDelay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static HyprlandSnapshot BuildUnavailableSnapshot()
    {
        var workspaces = Enumerable.Range(1, 5)
            .Select(id => new WorkspaceSnapshot(
                id,
                "fallback",
                id == 1,
                [],
                new PopupSnapshot($"workspace-{id}", $"Workspace {id}",
                    [new PopupRowSnapshot("(Hyprland IPC unavailable)", PopupRowKind.Action, false)])))
            .ToArray();

        return new HyprlandSnapshot(
            workspaces,
            [new MonitorWorkspaceSnapshot("fallback", true, 1, workspaces)],
            [],
            "Desktop",
            "",
            1,
            "",
            "",
            false);
    }

    private static (string? RequestSocketPath, string? EventSocketPath) ResolveSocketPaths()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        var signature = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
        if (string.IsNullOrWhiteSpace(runtime) || string.IsNullOrWhiteSpace(signature))
        {
            return (null, null);
        }

        var instanceDirectory = Path.Combine(runtime, "hypr", signature);
        return (
            Path.Combine(instanceDirectory, ".socket.sock"),
            Path.Combine(instanceDirectory, ".socket2.sock"));
    }
    
    private static T? Deserialize<T>(string? json, JsonTypeInfo<T> typeInfo)
    {
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize(json, typeInfo);
    }

    private static T[] DeserializeArray<T>(string? json, JsonTypeInfo<T[]> typeInfo)
    {
        return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize(json, typeInfo) ?? [];
    }

    private static T? DeserializeRequired<T>(string? json, JsonTypeInfo<T> typeInfo)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Hyprland returned an empty IPC response.");
        }

        return JsonSerializer.Deserialize(json, typeInfo);
    }

    private static T[] DeserializeArrayRequired<T>(string? json, JsonTypeInfo<T[]> typeInfo)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("Hyprland returned an empty IPC response.");
        }

        return JsonSerializer.Deserialize(json, typeInfo) ?? [];
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values.Where(x => string.IsNullOrWhiteSpace(x) == false))
        {
            return value!.Trim();
        }

        return "";
    }

}

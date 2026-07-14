using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.Hyprland;

internal sealed class HyprlandService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Task _eventTask;
    private readonly string? _requestSocketPath;
    private readonly string? _eventSocketPath;
    private HyprlandSnapshot _snapshot = HyprlandSnapshot.Empty;
    private bool _disposed;

    public HyprlandSnapshot Snapshot => _snapshot;

    public HyprlandService()
    {
        (_requestSocketPath, _eventSocketPath) = ResolveSocketPaths();
        _eventTask = RunEventLoopAsync(_cts.Token);
        _ = RefreshAsync(_cts.Token);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cts.Cancel();
        try
        {
            _eventTask.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch
        {
            // The event loop is best-effort and can be abandoned on shutdown.
        }

        _refreshLock.Dispose();
        _cts.Dispose();
        _disposed = true;
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
            catch
            {
                await DelayReconnect(cancellationToken);
            }
        }
    }

    private async Task HandleEventAsync(string line, CancellationToken cancellationToken)
    {
        Console.WriteLine(line);
        var lines = line.Split(">>");
        if (lines.Length != 2)
        {
            return;
        }

        var name = lines[0];
        var data = lines[1];

        if (string.Equals(name, "activelayout", StringComparison.Ordinal))
        {
            UpdateLayoutFromEvent(data);
            return;
        }

        if (RequiresSnapshotRefresh(name))
        {
            await RefreshAsync(cancellationToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!await _refreshLock.WaitAsync(0, cancellationToken))
        {
            return;
        }

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

            _snapshot = BuildSnapshot(active, clients, workspaces, monitors, devices);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch when (!_snapshot.Available)
        {
            _snapshot = BuildUnavailableSnapshot();
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

        var clientsByWorkspace = clients
            .Where(client => client.Workspace?.Id > 0)
            .GroupBy(client => client.Workspace!.Id)
            .ToDictionary(
                group => group.Key,
                group => group.Select(client => new WindowSummary(
                    client.ClassName,
                    FirstNonEmpty(client.Title, client.ClassName, "(untitled)"))).ToArray());

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
            Available = true,
        };
    }

    private static bool RequiresSnapshotRefresh(string name)
    {
        return name is
            "workspace" or
            "workspacev2" or
            "focusedmon" or
            "focusedmonv2" or
            "activewindow" or
            "activewindowv2" or
            "monitorremoved" or
            "monitorremovedv2" or
            "monitoradded" or
            "monitoraddedv2" or
            "createworkspace" or
            "createworkspacev2" or
            "destroyworkspace" or
            "destroyworkspacev2" or
            "moveworkspace" or
            "moveworkspacev2" or
            "renameworkspace" or
            "openwindow" or
            "closewindow" or
            "movewindow" or
            "movewindowv2" or
            "windowtitle" or
            "windowtitlev2";
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
    
    public static async Task HyprctlEvalAsync(string expression, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "hyprctl",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "eval", expression },
        });

        if (process is null)
        {
            Console.WriteLine("hyprctl eval failed: process did not start");
            return;
        }

        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var stderr = await stderrTask;

        if (process.ExitCode == 0)
        {
            Console.WriteLine("hyprctl eval succeeded");
            return;
        }

        Console.WriteLine($"hyprctl eval exited {process.ExitCode}: {stderr.Trim()}");
    }

    private static T? Deserialize<T>(string? json, JsonTypeInfo<T> typeInfo)
    {
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize(json, typeInfo);
    }

    private static T[] DeserializeArray<T>(string? json, JsonTypeInfo<T[]> typeInfo)
    {
        return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize(json, typeInfo) ?? [];
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

using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.KdeConnect;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class KdeConnectTab(KdeConnectService service) : IMainDialogTab, IDisposable
{
    private const int VisibleDeviceCount = 7;

    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private IReadOnlyList<KdeConnectDeviceSnapshot> _devices = [];
    private Task? _operationTask;
    private string? _status;
    private int _firstIndex;
    private int _selectedIndex;
    private bool _disposed;

    public string Id => "kde-connect";
    public string Title => "KDE Connect";
    public SvgAsset Icon => Icons.Smartphone;

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            _devices = service.Snapshot.Devices;
            _status = null;
            BoundedListUi.Normalize(
                ref _selectedIndex,
                ref _firstIndex,
                _devices.Count,
                VisibleDeviceCount);
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is not (SelectionDirection.Up or SelectionDirection.Down))
        {
            return;
        }

        lock (_stateLock)
        {
            BoundedListUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1,
                _devices.Count,
                VisibleDeviceCount);
        }
    }

    public void ActivateSelection()
    {
        KdeConnectDeviceSnapshot? device;
        lock (_stateLock)
        {
            device = _selectedIndex >= 0 && _selectedIndex < _devices.Count
                ? _devices[_selectedIndex]
                : null;
        }

        if (device is null)
        {
            return;
        }

        if (device.PairingState == KdeConnectPairingState.IncomingRequest)
        {
            AcceptPairing(device);
        }
        else if (device.IsPaired && device.IsReachable)
        {
            SendClipboard(device);
        }
        else if (!device.IsPaired && device.PairingState != KdeConnectPairingState.OutgoingRequest && device.IsReachable)
        {
            RequestPairing(device);
        }
    }

    public Node Draw()
    {
        IReadOnlyList<KdeConnectDeviceSnapshot> devices;
        string? status;
        int firstIndex;
        bool busy;
        lock (_stateLock)
        {
            _devices = service.Snapshot.Devices;
            BoundedListUi.Normalize(
                ref _selectedIndex,
                ref _firstIndex,
                _devices.Count,
                VisibleDeviceCount);
            devices = _devices;
            status = _status;
            firstIndex = _firstIndex;
            busy = _operationTask is { IsCompleted: false };
        }

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = new Style { Spacing = 12 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "KDE Connect",
                    devices.Count == 0
                        ? "No devices"
                        : $"{devices.Count} device{(devices.Count == 1 ? "" : "s")}"),
                ModulesCommon.BuildDivider(ThemeManager.Current.Border, height: 12),
                BuildDeviceList(devices, firstIndex, busy),
                MainDialogTabUi.BuildStatus(status),
            ],
        };
    }

    private Node BuildDeviceList(IReadOnlyList<KdeConnectDeviceSnapshot> devices, int firstIndex, bool busy)
    {
        if (devices.Count == 0)
        {
            return MainDialogTabUi.BuildMessage("No KDE Connect devices found");
        }

        return BoundedListUi.BuildList(
            devices,
            (device, index) => BuildDeviceRow(device, index, busy),
            firstIndex,
            VisibleDeviceCount);
    }

    private BoxNode BuildDeviceRow(KdeConnectDeviceSnapshot device, int index, bool busy)
    {
        var selected = index == _selectedIndex;
        var rowState = _rowStates.GetState(device.Id, ThemeManager.Current.Panel)
            .UpdateColor(selected ? Color.Lighten(ThemeManager.Current.Panel, 0.1f) : ThemeManager.Current.Panel);

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = rowState.Hovered,
            OnClick = () => Select(index),
            Style = ModulesCommon.ModuleStyle(rowState.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? ThemeManager.Current.Border.Width : 0,
                Spacing = 8,
            },
            Children =
            [
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new ImageNode(Icons.Smartphone, 18, 18, ThemeManager.Current.Text),
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        Style = new Style { Spacing = 4 },
                        Children =
                        [
                            new TextNode(device.Name, maxWidth: 300),
                            new TextNode(BuildDeviceDetails(device), 14, ThemeManager.Current.Text.MutedColor, maxWidth: 460),
                        ],
                    },
                },
                BuildActions(device, busy),
            ],
        };
    }

    private BoxNode BuildActions(KdeConnectDeviceSnapshot device, bool busy)
    {
        var actions = new List<Node>();
        if (device.PairingState == KdeConnectPairingState.IncomingRequest)
        {
            actions.Add(MainDialogTabUi.BuildButton(_buttonStates,
                "Accept",
                $"accept:{device.Id}",
                busy || string.IsNullOrWhiteSpace(device.VerificationCode)
                    ? null
                    : () => AcceptPairing(device)));
            actions.Add(MainDialogTabUi.BuildButton(_buttonStates,
                "Reject",
                $"reject:{device.Id}",
                busy ? null : () => RejectPairing(device)));
        }
        else if (device.IsPaired)
        {
            actions.Add(MainDialogTabUi.BuildButton(_buttonStates,
                "Send clipboard",
                $"clipboard:{device.Id}",
                !busy && device.IsReachable ? () => SendClipboard(device) : null));
            actions.Add(MainDialogTabUi.BuildButton(_buttonStates,
                "Unpair",
                $"unpair:{device.Id}",
                busy ? null : () => Unpair(device)));
        }
        else
        {
            var requestPending = device.PairingState == KdeConnectPairingState.OutgoingRequest;
            actions.Add(MainDialogTabUi.BuildButton(_buttonStates,
                requestPending ? "Pairing requested" : "Pair",
                $"pair:{device.Id}",
                !busy && !requestPending && device.IsReachable ? () => RequestPairing(device) : null));
        }

        return new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
        {
            Children = [.. actions],
        };
    }

    private static string BuildDeviceDetails(KdeConnectDeviceSnapshot device)
    {
        var details = new List<string> { device.DeviceType.ToString() };
        if (device.IsPaired)
        {
            details.Add(device.IsReachable ? "Connected" : "Paired offline");
            details.Add("Paired");
        }
        else if (device.PairingState == KdeConnectPairingState.IncomingRequest)
        {
            details.Add("Incoming pairing request");
        }
        else if (device.PairingState == KdeConnectPairingState.OutgoingRequest)
        {
            details.Add("Pairing requested");
        }
        else
        {
            details.Add(device.IsReachable ? "Available" : "Offline");
        }

        if (device.BatteryLevel is { } battery)
        {
            details.Add($"Battery {battery}%");
        }

        if (device.IsCharging == true)
        {
            details.Add("Charging");
        }

        if (!string.IsNullOrWhiteSpace(device.VerificationCode))
        {
            details.Add($"Code {device.VerificationCode}");
        }

        return string.Join(" · ", details);
    }

    private void Select(int index)
    {
        lock (_stateLock)
        {
            _selectedIndex = index;
        }
    }

    private void RequestPairing(KdeConnectDeviceSnapshot device) => StartOperation(
        async cancellationToken =>
        {
            var result = await service.RequestPairingAsync(device.Id, cancellationToken);
            return (result.Success, result.Error);
        },
        $"Requesting pairing with {device.Name}...");

    private void AcceptPairing(KdeConnectDeviceSnapshot device) => StartOperation(
        async cancellationToken =>
        {
            var result = await service.AcceptPairingAsync(device.Id, cancellationToken);
            return (result.Success, result.Error);
        },
        $"Accepting pairing request from {device.Name}...");

    private void RejectPairing(KdeConnectDeviceSnapshot device) => StartOperation(
        async cancellationToken =>
        {
            var result = await service.RejectPairingAsync(device.Id, cancellationToken);
            return (result.Success, result.Error);
        },
        $"Rejecting pairing request from {device.Name}...");

    private void Unpair(KdeConnectDeviceSnapshot device) => StartOperation(
        async cancellationToken =>
        {
            var result = await service.UnpairAsync(device.Id, cancellationToken);
            return (result.Success, result.Error);
        },
        $"Unpairing {device.Name}...");

    private void SendClipboard(KdeConnectDeviceSnapshot device) => StartOperation(
        async cancellationToken =>
        {
            var result = await service.SendClipboardAsync(device.Id, cancellationToken);
            return (result.Success, result.Error);
        },
        $"Sending clipboard to {device.Name}...");

    private void StartOperation(
        Func<CancellationToken, Task<(bool Success, string? Error)>> operation,
        string pendingStatus)
    {
        lock (_stateLock)
        {
            if (_disposed || _operationTask is { IsCompleted: false })
            {
                return;
            }

            _status = pendingStatus;
            _operationTask = RunOperationAsync(operation);
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task<(bool Success, string? Error)>> operation)
    {
        try
        {
            var result = await operation(_lifetime.Token);
            lock (_stateLock)
            {
                _status = result.Success ? "Done" : result.Error ?? "The KDE Connect operation failed";
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning("KDE Connect", "KDE Connect device operation failed", exception);
            lock (_stateLock)
            {
                _status = "The KDE Connect operation failed";
            }
        }
    }

    public void Dispose()
    {
        Task? operationTask;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            operationTask = _operationTask;
        }

        _lifetime.Cancel();
        operationTask?.Wait(TimeSpan.FromSeconds(2));
        _lifetime.Dispose();
    }
}

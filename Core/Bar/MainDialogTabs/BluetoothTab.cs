using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class BluetoothTab(BluetoothModuleService service, Theme theme) : IMainDialogTab, IDisposable
{
    private const int VisibleDeviceCount = 7;

    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private readonly Ref<float> _powerSwitchAnimation = new();
    private IReadOnlyList<BluetoothDeviceSnapshot> _devices = [];
    private BluetoothDeviceSnapshot? _pairDevice;
    private Task? _scanTask;
    private Task? _operationTask;
    private string? _status;
    private int _firstIndex;
    private int _selectedIndex;
    private bool? _poweredOverride;
    private bool _disposed;

    public string Id => "bluetooth";
    public string Title => "Bluetooth";
    public SvgAsset Icon => Icons.Bluetooth;

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            _devices = service.Snapshot.Devices;
            _pairDevice = null;
            _status = null;
            BoundedListUi.Normalize(
                ref _selectedIndex,
                ref _firstIndex,
                _devices.Count,
                VisibleDeviceCount);
        }

        if (EffectivePowered(service.Snapshot))
        {
            ScheduleScan();
        }
    }

    public void HandleTextInput(string text)
    {
    }

    public void HandleBackspace()
    {
    }

    public bool HandleEscape()
    {
        lock (_stateLock)
        {
            if (_pairDevice is null || _operationTask is { IsCompleted: false })
            {
                return false;
            }

            _pairDevice = null;
            _status = null;
            return true;
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
            if (_pairDevice is not null)
            {
                return;
            }

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
        BluetoothDeviceSnapshot? device;
        bool confirmPair;
        lock (_stateLock)
        {
            confirmPair = _pairDevice is not null;
            device = confirmPair
                ? _pairDevice
                : _selectedIndex >= 0 && _selectedIndex < _devices.Count
                    ? _devices[_selectedIndex]
                    : null;
        }

        if (device is null)
        {
            return;
        }

        if (confirmPair)
        {
            ConfirmPair();
        }
        else if (device.Paired)
        {
            BeginConnectionChange(device, !device.Connected);
        }
        else
        {
            ShowPairPopup(device);
        }
    }

    public Node Draw()
    {
        var snapshot = service.Snapshot;
        var powered = EffectivePowered(snapshot);
        IReadOnlyList<BluetoothDeviceSnapshot> devices;
        BluetoothDeviceSnapshot? pairDevice;
        string? status;
        int firstIndex;
        bool scanning;
        bool busy;
        lock (_stateLock)
        {
            devices = _devices;
            pairDevice = _pairDevice;
            status = _status;
            firstIndex = _firstIndex;
            scanning = _scanTask is { IsCompleted: false };
            busy = _operationTask is { IsCompleted: false };
        }

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = new Style { Spacing = 12 },
            Children = pairDevice is not null
                ? [BuildPairPopup(pairDevice, busy), MainDialogTabUi.BuildStatus(theme, status)]
                : [
                    BuildHeader(snapshot, powered, scanning, busy),
                    ..BuildDevices(snapshot, powered, devices, firstIndex, scanning, busy),
                    MainDialogTabUi.BuildStatus(theme, status)
                ],
        };
    }

    private BoxNode BuildHeader(BluetoothSnapshot snapshot, bool powered, bool scanning, bool busy) => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            new TextNode(
                powered ? "Bluetooth devices" : "Turn Bluetooth on to discover devices",
                theme.Text.HeaderSize,
                theme.Text),

            new BoxNode(new Style { Spacing = 16 }, verticalAlignment: ItemsAlignment.Center)
            {
                MainDialogTabUi.BuildButton(
                    theme,
                    _buttonStates,
                    scanning ? "Discovering..." : "Discover",
                    "discover",
                    powered && !scanning && !busy ? ScheduleScan : null),
                new BoxNode(2, 18) { Style = new Style { BackgroundColor = theme.Border } },
                new BoxNode
                {
                    OnClick = snapshot.Available && !busy ? () => SetPowered(!powered) : null,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = Style.Spacer,
                    Children =
                    [
                        new TextNode(powered ? "On" : "Off", theme.Text, theme.Text.MutedColor),
                        new SwitchNode(powered, _powerSwitchAnimation)
                        {
                            OffTrackColor = theme.Text.MutedColor,
                            OnTrackColor = theme.Active,
                            KnobColor = theme.Text,
                        },
                    ],
                },
            },
        ],
    };

    private IEnumerable<Node> BuildDevices(
        BluetoothSnapshot snapshot,
        bool powered,
        IReadOnlyList<BluetoothDeviceSnapshot> devices,
        int firstIndex,
        bool scanning,
        bool busy)
    {
        yield return ModulesCommon.BuildDivider(theme.Border, height: 12);
        if (!snapshot.Available)
        {
            yield return MainDialogTabUi.BuildMessage(theme, "Bluetooth is unavailable");
            yield break;
        }

        if (!powered)
        {
            yield return MainDialogTabUi.BuildMessage(theme, "Bluetooth is turned off");
            yield break;
        }

        if (devices.Count == 0)
        {
            yield return MainDialogTabUi.BuildMessage(
                theme,
                scanning ? "Discovering nearby devices..." : "No Bluetooth devices found");
            yield break;
        }

        var content = new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = Style.Spacer,
            Children = devices
                .VisibleItems(firstIndex, VisibleDeviceCount)
                .Select(item => BuildDeviceRow(item.Item, item.Index, busy))
                .ToArray(),
        };

        yield return BoundedListUi.BuildScrollableResults(
            content,
            firstIndex,
            devices.Count,
            VisibleDeviceCount,
            theme);
    }

    private BoxNode BuildDeviceRow(BluetoothDeviceSnapshot device, int index, bool busy)
    {
        var selected = index == _selectedIndex;
        var rowState = _rowStates.GetState(device.Address, theme.Panel)
            .UpdateColor(selected ? Color.Lighten(theme.Panel, 0.1f) : theme.Panel);
        var stateText = device.Connected ? "Connected" : device.Paired ? "Paired" : "Available";

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = rowState.Hovered,
            OnClick = () => _selectedIndex = index,
            Style = ModulesCommon.ModuleStyle(theme, rowState.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.Border.Width : 0,
                Spacing = 8,
            },
            Children =
            [
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new ImageNode(BluetoothUi.DeviceIcon(device.Icon), 18, 18, theme.Text),
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        Style = new Style { Spacing = 4 },
                        Children =
                        [
                            new TextNode(device.Name, theme.Text, theme.Text, maxWidth: 310),
                            new TextNode(device.BatteryPercentage is { } battery
                                ? $"{stateText} · Battery {battery}%"
                                : stateText, 14, theme.Text.MutedColor),
                        ],
                    },
                },
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    device.Paired
                        ? MainDialogTabUi.BuildButton(
                            theme,
                            _buttonStates,
                            device.Connected ? "Disconnect" : "Connect",
                            $"connect:{device.Address}",
                            busy ? null : () => BeginConnectionChange(device, !device.Connected))
                        : MainDialogTabUi.BuildButton(
                            theme,
                            _buttonStates,
                            "Pair",
                            $"pair:{device.Address}",
                            busy ? null : () => ShowPairPopup(device)),

                    busy == false ? MainDialogTabUi.BuildButton(
                        theme,
                        _buttonStates,
                        "Forget",
                        $"forget:{device.Address}",
                        device.Paired && !busy ? () => BeginForget(device) : null) : null,
                },
            ],
        };
    }

    private BoxNode BuildPairPopup(BluetoothDeviceSnapshot device, bool busy) => new BoxNode
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 14 },
        Children =
        [
            ModulesCommon.BuildTextWithIcon(
                theme,
                BluetoothUi.DeviceIcon(device.Icon),
                $"Pair with {device.Name}",
                maxTextWidth: 430),
            new TextNode(
                "Make sure the device is in pairing mode. Confirm any matching code shown on the device.",
                theme.Text,
                theme.Text,
                maxWidth: 620),
            new TextNode(device.Address, theme.Text, theme.Text),
            new BoxNode
            {
                HorizontalAlignment = ItemsAlignment.End,
                Style = Style.Spacer,
                Children =
                [
                    MainDialogTabUi.BuildButton(
                        theme, _buttonStates, "Cancel", "pair-cancel", busy ? null : CancelPair),
                    MainDialogTabUi.BuildButton(
                        theme,
                        _buttonStates,
                        busy ? "Pairing..." : "Pair",
                        "pair-confirm",
                        busy ? null : ConfirmPair),
                ],
            },
        ],
    };



    private void ShowPairPopup(BluetoothDeviceSnapshot device)
    {
        lock (_stateLock)
        {
            _pairDevice = device;
            _status = null;
        }
    }

    private void CancelPair()
    {
        lock (_stateLock)
        {
            _pairDevice = null;
            _status = null;
        }
    }

    private void ConfirmPair()
    {
        BluetoothDeviceSnapshot? device;
        lock (_stateLock)
        {
            device = _pairDevice;
        }

        if (device is not null)
        {
            StartOperation(
                cancellationToken => service.PairAsync(device.Address, cancellationToken),
                $"Pairing with {device.Name}...",
                closePairOnSuccess: true);
        }
    }

    private void BeginConnectionChange(BluetoothDeviceSnapshot device, bool connected) =>
        StartOperation(
            cancellationToken => service.SetConnectedAsync(device.Address, connected, cancellationToken),
            $"{(connected ? "Connecting to" : "Disconnecting from")} {device.Name}...");

    private void BeginForget(BluetoothDeviceSnapshot device) =>
        StartOperation(
            cancellationToken => service.ForgetAsync(device.Address, cancellationToken),
            $"Forgetting {device.Name}...");

    private void SetPowered(bool powered)
    {
        _poweredOverride = powered;
        if (!powered)
        {
            lock (_stateLock)
            {
                _devices = [];
            }
        }

        StartOperation(
            cancellationToken => service.SetPoweredAsync(powered, cancellationToken),
            $"Turning Bluetooth {(powered ? "on" : "off")}...");
    }

    private void StartOperation(
        Func<CancellationToken, Task<BluetoothOperationResult>> operation,
        string pendingStatus,
        bool closePairOnSuccess = false)
    {
        lock (_stateLock)
        {
            if (_operationTask is { IsCompleted: false })
            {
                return;
            }

            _status = pendingStatus;
            _operationTask = RunOperationAsync(operation, closePairOnSuccess);
        }
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task<BluetoothOperationResult>> operation,
        bool closePairOnSuccess)
    {
        var result = await operation(_lifetime.Token);
        lock (_stateLock)
        {
            _status = result.Success ? "Done" : result.Error ?? "The Bluetooth operation failed";
            if (result.Success && closePairOnSuccess)
            {
                _pairDevice = null;
            }
        }

        if (result.Success && EffectivePowered(service.Snapshot))
        {
            ScheduleScan();
        }
    }

    private void ScheduleScan()
    {
        lock (_stateLock)
        {
            if (_disposed || _scanTask is { IsCompleted: false })
            {
                return;
            }

            _status = "Discovering nearby devices...";
            _scanTask = ScanAsync();
        }
    }

    private async Task ScanAsync()
    {
        var devices = await service.ScanDevicesAsync(_lifetime.Token);
        lock (_stateLock)
        {
            _devices = devices;
            _status = null;
            BoundedListUi.Normalize(
                ref _selectedIndex,
                ref _firstIndex,
                devices.Count,
                VisibleDeviceCount);
        }
    }

    private bool EffectivePowered(BluetoothSnapshot snapshot)
    {
        if (_poweredOverride is not { } powered)
        {
            return snapshot.Powered;
        }

        if (powered != snapshot.Powered)
        {
            return powered;
        }

        _poweredOverride = null;
        return snapshot.Powered;
    }



    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Task? scanTask;
        Task? operationTask;
        lock (_stateLock)
        {
            _disposed = true;
            scanTask = _scanTask;
            operationTask = _operationTask;
        }

        _lifetime.Cancel();
        try
        {
            Task.WaitAll([scanTask ?? Task.CompletedTask, operationTask ?? Task.CompletedTask], TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _lifetime.Dispose();
    }
}

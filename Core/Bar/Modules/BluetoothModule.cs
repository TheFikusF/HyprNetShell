using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class BluetoothModule(
    BluetoothModuleService service,
    Theme theme,
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private readonly Dictionary<string, bool> _connectionOverrides = [];
    private readonly Ref<float> _powerSwitchAnimation = new();
    private bool? _poweredOverride;

    private readonly NodeWithPopup _node = new(popupCoordinator, "bluetooth_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var bluetooth = service.Snapshot;
        return _node.Draw([BuildStateModule(bluetooth)], () => BuildPopup(bluetooth));
    }

    private Node BuildStateModule(BluetoothSnapshot bluetooth)
    {
        var powered = EffectivePowered(bluetooth);
        var connectedCount = powered ? bluetooth.Devices.Count(EffectiveConnected) : 0;
        var icon = !bluetooth.Available || !powered
            ? Icons.BluetoothOff
            : connectedCount == 0
                ? Icons.Bluetooth
                : Icons.BluetoothConnected;

        var bg = ModulesCommon.ToBackground(theme, Color.Lerp(Color.Lazure, Color.Blue, 0.3f));
        return ModulesCommon.BuildTextWithIcon(theme, icon, connectedCount.ToString(),
            style: ModulesCommon.ModuleStyle(theme, bg, false, false) with
            {
                BorderWidth = new Insets(1, theme.BorderWidth),
                ShadowColor = null
            }, width: 55);
    }

    private BoxNode BuildPopup(BluetoothSnapshot bluetooth) => new(360)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            BuildPowerRow(bluetooth),
            ..BuildDeviceRows(bluetooth, EffectivePowered(bluetooth)),
        ],
    };

    private BoxNode BuildPowerRow(BluetoothSnapshot bluetooth)
    {
        var powered = EffectivePowered(bluetooth);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = bluetooth.Available ? () => SetPowered(bluetooth, !powered) : null,
            Style = new Style
            {
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new BoxNode(48),
                ModulesCommon.BuildTextWithIcon(theme, Icons.Bluetooth, "Bluetooth"),
                new SwitchNode(powered, _powerSwitchAnimation)
                {
                    OffTrackColor = theme.Muted,
                    OnTrackColor = theme.Active,
                    KnobColor = theme.Text,
                },
            ],
        };
    }

    private IEnumerable<Node> BuildDeviceRows(BluetoothSnapshot bluetooth, bool powered)
    {
        if (!bluetooth.Available)
        {
            yield return BuildPlainRow("Bluetooth unavailable");
            yield break;
        }

        if (!powered)
        {
            yield return BuildPlainRow("Bluetooth is off");
            yield break;
        }

        if (bluetooth.Devices.Count == 0)
        {
            yield return BuildPlainRow("No paired devices");
        }

        foreach (var device in bluetooth.Devices.Take(8))
        {
            yield return BuildDeviceRow(device);
        }
    }

    private BoxNode BuildDeviceRow(BluetoothDeviceSnapshot device)
    {
        var connected = EffectiveConnected(device);
        var state = _rowStates.GetState(device.Address, theme.Panel)
            .UpdateColor(connected ? theme.Active : theme.Panel);
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => ToggleConnection(device),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = connected ? theme.BorderWidth : 0,
            },
            Children =
            [
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Children =
                    [
                        ModulesCommon.BuildTextWithIcon(theme, DeviceTypeIcon(device.Icon), Trim(device.Name, 30)),
                        new TextNode(connected ? "Connected" : "Disconnected", theme.TextSize, theme.Text),
                    ]
                },
                device.BatteryPercentage is { } battery
                    ? new BoxNode
                    {
                        HorizontalAlignment = ItemsAlignment.Spread,
                        VerticalAlignment = ItemsAlignment.Center,
                        Style = new Style { Padding = new Insets(8, 0, 0, 0) },
                        Children =
                        [
                            new TextNode("Battery", theme.TextSize, theme.Text),
                            ModulesCommon.BuildTextWithIcon(theme, BatteryModule.BatteryLevelIcon(battery),
                                $"{battery}%",
                                battery <= 20 ? Color.Lerp(Color.White, Color.Orange, 0.5f) : theme.Text)
                        ]
                    }
                    : new SpacerNode(),
            ],
        };
    }

    private BoxNode BuildPlainRow(string text) => new()
    {
        Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
        Children = [new TextNode(text, theme.TextSize, theme.Muted)],
    };

    private bool EffectiveConnected(BluetoothDeviceSnapshot device)
    {
        if (_connectionOverrides.TryGetValue(device.Address, out var connected) == false)
        {
            return device.Connected;
        }

        if (connected != device.Connected)
        {
            return connected;
        }

        _connectionOverrides.Remove(device.Address);
        return device.Connected;
    }

    private bool EffectivePowered(BluetoothSnapshot bluetooth)
    {
        if (_poweredOverride is not { } powered)
        {
            return bluetooth.Powered;
        }

        if (powered != bluetooth.Powered)
        {
            return powered;
        }

        _poweredOverride = null;
        return bluetooth.Powered;
    }

    private void SetPowered(BluetoothSnapshot bluetooth, bool powered)
    {
        if (!bluetooth.Available)
        {
            return;
        }

        _poweredOverride = powered;
        _ = service.SetPoweredAsync(powered);
    }

    private void ToggleConnection(BluetoothDeviceSnapshot device)
    {
        var connect = !EffectiveConnected(device);
        _connectionOverrides[device.Address] = connect;
        _ = service.SetConnectedAsync(device.Address, connect);
    }

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";

    private static SvgAsset DeviceTypeIcon(string? icon) => icon?.ToLowerInvariant() switch
    {
        "audio-headphones" => Icons.Headphones,
        "audio-headset" => Icons.Headset,
        "audio-speakers" or "audio-card" => Icons.Speaker,
        "audio-input-microphone" => Icons.Microphone,
        "input-keyboard" => Icons.Keyboard,
        "input-mouse" => Icons.Mouse,
        "input-gaming" => Icons.Gamepad,
        "input-tablet" => Icons.Tablet,
        "phone" => Icons.Smartphone,
        "computer" => Icons.Laptop,
        "multimedia-player" => Icons.Monitor,
        "watch" => Icons.Watch,
        "camera-photo" or "camera-video" => Icons.Camera,
        "printer" or "scanner" => Icons.Printer,
        _ => Icons.Bluetooth,
    };
}

using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class BluetoothModule(
    Func<BluetoothSnapshot> snapshot,
    Theme theme) : IDrawableModule
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private readonly Dictionary<string, bool> _connectionOverrides = [];

    private readonly ModulesCommon.NodeWithPopup _node = new("bluetooth_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var bluetooth = snapshot();
        return _node.Draw([BuildStateModule(bluetooth)], () => BuildPopup(bluetooth));
    }

    private Node BuildStateModule(BluetoothSnapshot bluetooth)
    {
        var connectedCount = bluetooth.Devices.Count(EffectiveConnected);
        var icon = !bluetooth.Available
            ? Icons.BluetoothOff
            : connectedCount == 0
                ? Icons.Bluetooth
                : Icons.BluetoothConnected;
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme,
                    ModulesCommon.ToBackground(theme, Color.Lerp(Color.Lazure, Color.Blue, 0.3f)),
                    left: false, right: false) with
                {
                    BorderWidth = new Insets(1, theme.BorderWidth),
                    Spacing = 6,
                },
            Children =
            [
                new ImageNode(icon, 18, 18, theme.Text),
                ..(connectedCount > 0
                    ? new Node[] { new TextNode(connectedCount.ToString(), 14.0f, theme.Text) }
                    : Array.Empty<Node>()),
            ],
        };
    }

    private BoxNode BuildPopup(BluetoothSnapshot bluetooth) => new (360)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            new TextNode("Bluetooth devices", 14.0f, theme.Text),
            ..BuildDeviceRows(bluetooth),
        ],
    };

    private IEnumerable<Node> BuildDeviceRows(BluetoothSnapshot bluetooth)
    {
        if (!bluetooth.Available)
        {
            yield return BuildPlainRow("Bluetooth unavailable");
            yield break;
        }

        if (bluetooth.Devices.Count == 0)
        {
            yield return BuildPlainRow("No paired devices");
            yield break;
        }

        foreach (var device in bluetooth.Devices.Take(8))
        {
            yield return BuildDeviceRow(device);
        }
    }

    private Node BuildDeviceRow(BluetoothDeviceSnapshot device)
    {
        var connected = EffectiveConnected(device);
        var state = GetRowState(device.Address, connected ? theme.Active : theme.Panel);
        var baseColor = connected ? theme.Active : theme.Panel;
        var target = state.Hovered ? Color.Lighten(baseColor, 0.12f) : baseColor;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, ModulesCommon.DELTA_TIME);

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

    private Node BuildPlainRow(string text) =>
        new BoxNode
        {
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
            Children = [new TextNode(text, 13.0f, theme.Muted)],
        };

    private bool EffectiveConnected(BluetoothDeviceSnapshot device)
    {
        if (_connectionOverrides.TryGetValue(device.Address, out var connected))
        {
            if (connected == device.Connected)
            {
                _connectionOverrides.Remove(device.Address);
            }
            else
            {
                return connected;
            }
        }

        return device.Connected;
    }

    private void ToggleConnection(BluetoothDeviceSnapshot device)
    {
        var connect = !EffectiveConnected(device);
        _connectionOverrides[device.Address] = connect;
        _ = BluetoothModuleService.SetConnectedAsync(device.Address, connect);
    }

    private ModulesCommon.BoxState GetRowState(string key, Color initialColor)
    {
        if (_rowStates.TryGetValue(key, out var state))
        {
            return state;
        }

        state = new ModulesCommon.BoxState(initialColor);
        _rowStates[key] = state;
        return state;
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
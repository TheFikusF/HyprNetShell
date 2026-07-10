using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal sealed class BluetoothModule(
    Func<BluetoothSnapshot> snapshot,
    StatusBarTheme theme) : IDrawableModule
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
        var label = !bluetooth.Available ? "󰂲" : connectedCount == 0 ? "󰂯" : $"󰂱 {connectedCount}";
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel, left: false, right: false) with
            {
                BorderWidth = new Insets(1, theme.BorderWidth)
            },
            Children = [new TextNode(label, 14.0f, theme.Text)],
        };
    }

    private BoxNode BuildPopup(BluetoothSnapshot bluetooth) =>
        new(360)
        {
            IgnoreLayout = true,
            Style = new Style { Padding = new Insets(32, 0, 0, 0) },
            Children =
            [
                new BoxNode(360)
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Start,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Style = new Style
                    {
                        BackgroundColor = Color.FromRgb(0, 0, 0, 0.94f),
                        BorderColor = theme.Border,
                        BorderRadius = 8,
                        BorderWidth = 2,
                        Padding = 8,
                        Spacing = 8,
                    },
                    Children =
                    [
                        new TextNode("Bluetooth devices", 14.0f, theme.Text),
                        ..BuildDeviceRows(bluetooth),
                    ],
                },
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
            HorizontalAlignment = ItemsAlignment.Spread,
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
                        new TextNode(Trim(device.Name, 30), 14.0f, theme.Text),
                        new TextNode(connected ? "Connected" : "Disconnected", 12.0f, theme.Text),
                    ]
                },
                device.BatteryPercentage is { } battery
                    ? new BoxNode
                    {
                        HorizontalAlignment = ItemsAlignment.Spread,
                        VerticalAlignment = ItemsAlignment.Center,
                        Style = new Style() { Padding = new Insets(8, 0, 0, 0) },
                        Children =
                        [
                            new TextNode("Battery", 12.0f, theme.Text),
                            new TextNode($"{battery}%", 14.0f, battery <= 20 ? theme.Warning : theme.Text),
                        ]
                    }
                    : new SpacerNode(),
            ],
        };
    }

    private Node BuildBatteryRow(int battery) =>
        new BoxNode(340)
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Padding = new Insets(2, 8) },
            Children =
            [
                new TextNode("Battery", 12.0f, theme.Muted),
                new TextNode($"{battery}%", 13.0f, battery <= 20 ? theme.Warning : theme.Text),
            ],
        };

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
}
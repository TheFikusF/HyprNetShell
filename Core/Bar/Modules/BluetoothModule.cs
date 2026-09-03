using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;
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
    DialogService dialogs,
    TabsService tabs,
    Theme theme,
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private readonly ModulesCommon.BoxState _settingsState = new();
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
                BorderWidth = new Insets(1, theme.Border.Width),
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
        _settingsState.UpdateColor(theme.Panel);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style
            {
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new BoxNode(76),
                ModulesCommon.BuildTextWithIcon(theme, Icons.Bluetooth, "Bluetooth"),
                new BoxNode(Style.Spacer, ItemsAlignment.Center, ItemsAlignment.Center)
                {
                    new BoxNode
                    {
                        HorizontalAlignment = ItemsAlignment.Center,
                        VerticalAlignment = ItemsAlignment.Center,
                        IsHovered = _settingsState.Hovered,
                        OnClick = bluetooth.Available ? OpenBluetoothDevices : null,
                        Style = ModulesCommon.ModuleStyle(theme, _settingsState.Background) with
                        {
                            Padding = 4,
                            BorderRadius = 8,
                            BorderWidth = 0,
                        },
                        Children = [new ImageNode(Icons.Settings, 20, 20, theme.Text)],
                    },
                    new BoxNode
                    {
                        OnClick = bluetooth.Available ? () => SetPowered(bluetooth, !powered) : null,
                        Children =
                        [
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
    }

    private void OpenBluetoothDevices()
    {
        _node.ClosePopup();
        dialogs.Open<CompositeWindow>([tabs.Get<BluetoothTab>()]);
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
                BorderWidth = connected ? theme.Border.Width : 0,
            },
            Children =
            [
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Children =
                    [
                        ModulesCommon.BuildTextWithIcon(
                            theme,
                            BluetoothUi.DeviceIcon(device.Icon),
                            device.Name,
                            maxTextWidth: 190),
                        new TextNode(connected ? "Connected" : "Disconnected", theme.Text, theme.Text),
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
                            new TextNode("Battery", theme.Text, theme.Text),
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
        Children = [new TextNode(text, theme.Text, theme.Text.MutedColor)],
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


}

using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Native.Interop.Authorization;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class BatteryModule : IDrawableModule, IDisposable
{
    private const string POPUP_ID = "battery_module";
    private const int BATTERY_TRACK_WIDTH = 74;

    private readonly BatteryModuleService _service;
    private readonly DeviceBatteryService _deviceBatteries;
    private readonly PopupCoordinator _popupCoordinator;
    private readonly NodeWithPopup _node;
    private readonly SegmentedSwitch _powerProfileSwitch = new();
    private readonly ModulesCommon.BoxState _chargeLimitDecreaseState = new();
    private readonly ModulesCommon.BoxState _chargeLimitIncreaseState = new();

    private readonly Gradient _batteryGradient = new([
        new Gradient.Stop(0, Color.Red),
        new Gradient.Stop(0.15f, Color.Red),
        new Gradient.Stop(0.3f, Color.Yellow),
        new Gradient.Stop(0.5f, Color.FromRgb(46, 204, 113)),
        new Gradient.Stop(1f, Color.FromRgb(46, 204, 113))
    ]);

    private readonly Gradient _batteryOverlayGradient = new(
        new Gradient.Stop(0.0f, Color.FromRgb(255, 255, 255, 0.35f)),
        // new Gradient.Stop(0.33f, Color.FromRgb(255, 255, 255, 0.08f)),
        new Gradient.Stop(0.5f, Color.FromRgb(255, 255, 255, 0.0f)),
        // new Gradient.Stop(0.66f, Color.FromRgb(0, 0, 0, 0.08f)),
        new Gradient.Stop(1.0f, Color.FromRgb(0, 0, 0, 0.45f)));

    private ChargeLimitSession? _chargeLimitSession;
    private CancellationTokenSource? _popupLifetime;
    private Task? _chargeLimitWrite;
    private bool _disposed;

    private bool ChargeLimitWritePending => _chargeLimitWrite is { IsCompleted: false };

    public BatteryModule(
        BatteryModuleService service,
        DeviceBatteryService deviceBatteries,
        PopupCoordinator popupCoordinator)
    {
        _service = service;
        _deviceBatteries = deviceBatteries;
        _popupCoordinator = popupCoordinator;
        _node = new NodeWithPopup(popupCoordinator, POPUP_ID) {
            HorizontalAlignment = ItemsAlignment.Center,
            // The authorization dialog can take hover away before the request completes.
            GetShouldShowPopup = hovered => hovered ||
                (_chargeLimitSession is not null && ChargeLimitWritePending && _popupCoordinator.IsOpen(POPUP_ID)),
        };
    }

    public Node Draw()
    {
        var battery = _service.Snapshot;
        if (_disposed || !battery.Available)
        {
            CloseChargeLimitSession();
            return new SpacerNode();
        }

        if (battery.ChargeLimit is null || !_popupCoordinator.IsOpen(POPUP_ID) || !_node.ShouldShowPopup)
        {
            CloseChargeLimitSession();
        }

        var devices = _deviceBatteries.Snapshot;
        return _node.Draw([BuildStateModule(battery)], () => BuildPopup(battery, devices));
    }

    public void Dispose()
    {
        _disposed = true;
        CloseChargeLimitSession();
        _node.ClosePopup();
    }

    private void CloseChargeLimitSession()
    {
        _popupLifetime?.Cancel();
        _chargeLimitSession?.Dispose();
        _chargeLimitSession = null;
        _popupLifetime?.Dispose();
        _popupLifetime = null;
    }

    private void AdjustChargeLimit(int delta)
    {
        if (_disposed || ChargeLimitWritePending || !_popupCoordinator.IsOpen(POPUP_ID) ||
            !_node.ShouldShowPopup || _service.Snapshot is not { Available: true, ChargeLimit: { } limit })
        {
            return;
        }

        _popupLifetime ??= new CancellationTokenSource();
        _chargeLimitSession ??= _service.BeginChargeLimitSession();
        _chargeLimitWrite = _service.SetChargeLimitAsync(limit + delta, _chargeLimitSession, _popupLifetime.Token);
    }

    private BoxNode BuildStateModule(BatterySnapshot battery)
    {
        var percentage = battery.Percentage;
        // var percentage = (float)(Environment.TickCount64 % 2600 / 2600.0) * 100;

        return new BoxNode(new Style(), verticalAlignment: ItemsAlignment.Center)
        {
            new BoxNode(80, 14 + 8 + 8)
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Start,
                Children =
                [
                    new BoxNode(BATTERY_TRACK_WIDTH, 14 + 5 + 5)
                    {
                        IgnoreLayout = true,
                        Left = (int)ThemeManager.Current.Border.Width,
                        Style = new Style
                        {
                            BackgroundColor = Color.FromRgb(0, 0, 0, 0.5f),
                        }
                    },
                    BuildBatteryFill(battery.IsCharging, percentage),
                    new BoxNode(BATTERY_TRACK_WIDTH, 14 + 5 + 5)
                    {
                        IgnoreLayout = true,
                        Direction = Direction.Horizontal,
                        VerticalAlignment = ItemsAlignment.Center,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Left = (int)ThemeManager.Current.Border.Width,
                        Style = new Style { Spacing = 2, Padding = new Insets(2, 2) },
                        Children = [
                            BuildBatteryBlock(battery.IsCharging, percentage, 0),
                            BuildBatteryBlock(battery.IsCharging, percentage, 1),
                            BuildBatteryBlock(battery.IsCharging, percentage, 2),
                            BuildBatteryBlock(battery.IsCharging, percentage, 3),
                        ]
                    },
                    // BuildBatteryFill(true, percentage),
                    new GradientBoxNode(new Color(), Color.FromRgb(0, 0, 0, 0.35f), () => 0.0f, 80, 14 + 5 + 5)
                    {
                        IgnoreLayout = true,
                        GradientDirection = GradientDirection.Horizontal,
                    },
                    ..BuildChargeLimitMarker(battery.ChargeLimit),
                    new GradientBoxNode(_batteryOverlayGradient, 80, 14 + 5 + 5)
                    {
                        IgnoreLayout = true,
                        Direction = Direction.Horizontal,
                        GradientDirection = GradientDirection.Vertical,
                        HorizontalAlignment = ItemsAlignment.Center,
                        VerticalAlignment = ItemsAlignment.Center,
                        Style = new Style
                        {
                            BorderRadius = 3,
                            Spacing = 4,
                            Padding = new Insets(0, 0, 2, 0)
                        },
                        Children = battery.IsCharging
                            ?
                            [
                                new ImageNode(Icons.Lightning, 16, 16, ThemeManager.Current.Text),
                                new TextNode($"{percentage:0}%") { ShadowColor = Color.Black with { A = 0.8f }, ShadowDistance = 2 }
                            ]
                            : [new TextNode($"{percentage:0}%") { ShadowColor = Color.Black with { A = 0.8f }, ShadowDistance = 2 }]
                    },
                ],
            },
            new BoxNode(4, 16)
            {
                Style = new Style
                {
                    BackgroundColor = ThemeManager.Current.Border,
                    BorderRadius = new BorderRadius(0, 4, 4, 0),
                    ShadowColor = Color.Black with { A = 0.45f },
                    ShadowDistance = 5.0f
                }
            },
            new BoxNode(80, 14 + 8 + 8)
            {
                IgnoreLayout = true,
                Style = new Style
                {
                    BorderRadius = 8,
                    BorderWidth = ThemeManager.Current.Border.Width,

                    ShadowColor = Color.Black with { A = 0.45f },
                    ShadowDistance = 5.0f
                }
            },
        };
    }

    private static IEnumerable<Node> BuildChargeLimitMarker(int? chargeLimit)
    {
        if (chargeLimit is not (> 0 and < 100))
        {
            yield break;
        }

        var dashStyle = new Style { BackgroundColor = Color.FromRgb(255, 255, 255, 0.7f) };
        yield return new BoxNode(1, 18) {
            IgnoreLayout = true,
            Left = (int)ThemeManager.Current.Border.Width + BATTERY_TRACK_WIDTH * chargeLimit.Value / 100,
            Direction = Direction.Vertical,
            Style = new Style { Spacing = 2 },
            Children =
            [
                new BoxNode(1, 3) { Style = dashStyle },
                new BoxNode(1, 3) { Style = dashStyle },
                new BoxNode(1, 3) { Style = dashStyle },
                new BoxNode(1, 3) { Style = dashStyle },
            ],
        };
    }

    private BoxNode BuildBatteryFill(bool isCharging, float percentage)
    {
        var width = (int)(BATTERY_TRACK_WIDTH * (percentage / 100));
        var color = _batteryGradient.Evaluate(percentage / 100);
        // if (isCharging)
        // {
        //     return new GradientBoxNode(color, Color.Darken(color, 0.3f), ChargingGradientOffset, width, 14 + 5 + 5)
        //     {
        //         IgnoreLayout = true,
        //         Left = (int)ThemeManager.Current.Border.Width,
        //         Direction = Direction.Horizontal,
        //         GradientDirection = GradientDirection.Horizontal,
        //     };
        // }

        return new BoxNode(width, 14 + 5 + 5) {
            IgnoreLayout = true,
            Left = (int)ThemeManager.Current.Border.Width,
            Style = new Style { BackgroundColor = Color.Darken(color, 0.5f) }
        };
    }

    private BoxNode BuildBatteryBlock(bool isCharging, float percentage, int index)
    {
        const int BLOCK_WIDTH = (BATTERY_TRACK_WIDTH - (2 * 2) - (2 * 3)) / 4;
        percentage /= 100;
        var color = _batteryGradient.Evaluate(percentage);
        percentage = MathF.Max(MathF.Min((percentage * 4) - index, 1), 0);
        var width = (int)(BLOCK_WIDTH * percentage);
        if (isCharging)
        {
            return new GradientBoxNode(color, Color.Darken(color, 0.3f), ChargingGradientOffset, width, 14 + 5 + 5 - 6) {
                IgnoreLayout = true,
                Left = (BLOCK_WIDTH + 2) * index,
                Direction = Direction.Horizontal,
                GradientDirection = GradientDirection.Horizontal,
                Style = new Style { BackgroundColor = color, BorderRadius = 4 }
            };
        }

        return new BoxNode(width, 14 + 5 + 5 - 6) {
            IgnoreLayout = true,
            Left = (BLOCK_WIDTH + 2) * index,
            Style = new Style { BackgroundColor = color, BorderRadius = 4 }
        };
    }

    private static float ChargingGradientOffset() =>
        -(float)(Environment.TickCount64 % 6000 / 6000.0);

    private BoxNode BuildPopup(BatterySnapshot battery, DeviceBatteriesSnapshot devices) => new() {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(),
        Children =
        [
            ModulesCommon.BuildTextWithIcon(Icons.Info, "Battery"),
            BuildRow("Device", battery.Device),
            new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
            {
                new TextNode("Capacity"),
                ModulesCommon.BuildTextWithIcon(BatteryLevelIcon(battery.Percentage), $"{battery.Percentage}%"),
            },
            BuildRow("Status", battery.Status),
            ..BuildChargeLimitControl(battery.ChargeLimit),
            ..BuildPowerProfileSection(battery.PowerProfiles),
            ..BuildDeviceBatterySection(devices.Devices),
        ],
    };

    private IEnumerable<Node> BuildChargeLimitControl(int? chargeLimit)
    {
        if (chargeLimit is not { } limit)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(ThemeManager.Current.Border, height: 16);
        yield return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 24 },
            Children =
            [
                new TextNode("Charge limit"),
                new BoxNode
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 4 },
                    Children =
                    [
                        BuildChargeLimitButton("-", -BatteryModuleService.CHARGE_LIMIT_STEP,
                            _chargeLimitDecreaseState, limit > BatteryModuleService.MINIMUM_CHARGE_LIMIT),
                        new BoxNode(48, 34)
                        {
                            HorizontalAlignment = ItemsAlignment.Center,
                            VerticalAlignment = ItemsAlignment.Center,
                            Children = [new TextNode($"{limit}%")],
                        },
                        BuildChargeLimitButton("+", BatteryModuleService.CHARGE_LIMIT_STEP,
                            _chargeLimitIncreaseState, limit < BatteryModuleService.MAXIMUM_CHARGE_LIMIT),
                    ],
                },
            ],
        };
    }

    private BoxNode BuildChargeLimitButton(
        string label,
        int delta,
        ModulesCommon.BoxState buttonState,
        bool enabled)
    {
        enabled = enabled && !ChargeLimitWritePending && _popupCoordinator.IsOpen(POPUP_ID) && _node.ShouldShowPopup;
        var state = buttonState.UpdateColor(ThemeManager.Current.Text.MutedColor);
        return new BoxNode() {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = buttonState.Hovered,
            OnClick = enabled
                ? () => AdjustChargeLimit(delta)
                : null,
            Style = ModulesCommon.ModuleStyle(enabled ? state.Background : ThemeManager.Current.Panel) with {
                Padding = 6,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(label, 14, enabled ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor)],
        };
    }

    private IEnumerable<Node> BuildPowerProfileSection(PowerProfileSnapshot powerProfiles)
    {
        if (!powerProfiles.Available)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(ThemeManager.Current.Border, height: 16);
        yield return new TextNode("Power profile");
        yield return _powerProfileSwitch.Build(powerProfiles.Profiles
                .Select(profile => new SegmentedSwitch.Item(
                    profile,
                    new ImageNode(ProfileLabel(profile), 16, 16, ThemeManager.Current.Text)))
                .ToArray(),
            powerProfiles.Active,
            _service.SetPowerProfile);
    }

    private IEnumerable<Node> BuildDeviceBatterySection(IReadOnlyList<DeviceBatterySnapshot> devices)
    {
        if (devices.Count == 0)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(ThemeManager.Current.Border, height: 16);
        yield return new TextNode("Device batteries");
        foreach (var device in devices)
        {
            var color = device.Percentage <= DeviceBatteryService.LOW_BATTERY_PERCENTAGE && device.IsCharging is not true
                ? ThemeManager.Current.Critical
                : ThemeManager.Current.Text;
            yield return new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
            {
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    Style = new Style { Spacing = 2 },
                    Children =
                    [
                        new TextNode(device.Name),
                        new TextNode(SourceLabel(device.Source), ThemeManager.Current.Text.SmallSize, ThemeManager.Current.Text.MutedColor),
                    ],
                },
                ModulesCommon.BuildTextWithIcon(device.IsCharging is true ? Icons.BatteryCharging : BatteryLevelIcon(device.Percentage),
                    $"{device.Percentage}%",
                    color),
            };
        }
    }

    private static string SourceLabel(DeviceBatterySource source) => source switch {
        DeviceBatterySource.Laptop => "Laptop battery",
        DeviceBatterySource.Bluetooth => "Bluetooth",
        DeviceBatterySource.KdeConnect => "KDE Connect",
        _ => "Device",
    };

    private static SvgAsset ProfileLabel(string profile) => profile switch {
        "power-saver" => Icons.Leaf,
        "balanced" => Icons.Scale,
        "performance" => Icons.Flame,
        _ => Icons.Scale,
    };

    private BoxNode BuildRow(string label, string value) =>
        new(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
        {
            new TextNode(label),
            new TextNode(value),
        };

    public static SvgAsset BatteryLevelIcon(int percentage) => Icons.BatteryLevels[percentage switch {
        <= 10 => 0,
        <= 35 => 1,
        <= 70 => 2,
        _ => 3,
    }];
}

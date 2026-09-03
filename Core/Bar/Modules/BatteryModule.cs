using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class BatteryModule(BatteryModuleService service, Theme theme,
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _profileStates = [];
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

    private readonly NodeWithPopup _node = new(popupCoordinator, "battery_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var battery = service.Snapshot;
        return battery.Available
            ? _node.Draw([BuildStateModule(battery)], () => BuildPopup(battery))
            : new SpacerNode();
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
                    new BoxNode(74, 14 + 5 + 5)
                    {
                        IgnoreLayout = true,
                        Left = (int)theme.Border.Width,
                        Style = new Style
                        {
                            BackgroundColor = Color.FromRgb(0, 0, 0, 0.5f),
                        }
                    },
                    BuildBatteryFill(battery.IsCharging, percentage),
                    new BoxNode(74, 14 + 5 + 5)
                    {
                        IgnoreLayout = true,
                        Direction = Direction.Horizontal,
                        VerticalAlignment = ItemsAlignment.Center,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Left = (int)theme.Border.Width,
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
                                new ImageNode(Icons.Lightning, 16, 16, theme.Text),
                                new TextNode($"{percentage:0}%", theme.Text, theme.Text) { ShadowColor = Color.Black with { A = 0.8f }, ShadowDistance = 2 }
                            ]
                            : [new TextNode($"{percentage:0}%", theme.Text, theme.Text) { ShadowColor = Color.Black with { A = 0.8f }, ShadowDistance = 2 }]
                    },
                ],
            },
            new BoxNode(4, 16)
            {
                Style = new Style
                {
                    BackgroundColor = theme.Border,
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
                    BorderWidth = theme.Border.Width,
                    BorderColor = theme.Border,
                    ShadowColor = Color.Black with { A = 0.45f },
                    ShadowDistance = 5.0f
                }
            },
        };
    }

    private BoxNode BuildBatteryFill(bool isCharging, float percentage)
    {
        var width = (int)(74 * (percentage / 100));
        var color = _batteryGradient.Evaluate(percentage / 100);
        // if (isCharging)
        // {
        //     return new GradientBoxNode(color, Color.Darken(color, 0.3f), ChargingGradientOffset, width, 14 + 5 + 5)
        //     {
        //         IgnoreLayout = true,
        //         Left = (int)theme.Border.Width,
        //         Direction = Direction.Horizontal,
        //         GradientDirection = GradientDirection.Horizontal,
        //     };
        // }

        return new BoxNode(width, 14 + 5 + 5)
        {
            IgnoreLayout = true,
            Left = (int)theme.Border.Width,
            Style = new Style { BackgroundColor = Color.Darken(color, 0.5f) }
        };
    }

    private BoxNode BuildBatteryBlock(bool isCharging, float percentage, int index)
    {
        const int BLOCK_WIDTH = (74 - (2 * 2) - (2 * 3)) / 4;
        percentage /= 100;
        var color = _batteryGradient.Evaluate(percentage);
        percentage = MathF.Max(MathF.Min((percentage * 4) - index, 1), 0);
        var width = (int)(BLOCK_WIDTH * percentage);
        if (isCharging)
        {
            return new GradientBoxNode(color, Color.Darken(color, 0.3f), ChargingGradientOffset, width, 14 + 5 + 5 - 6)
            {
                IgnoreLayout = true,
                Left = (BLOCK_WIDTH + 2) * index,
                Direction = Direction.Horizontal,
                GradientDirection = GradientDirection.Horizontal,
                Style = new Style { BackgroundColor = color, BorderRadius = 4 }
            };
        }

        return new BoxNode(width, 14 + 5 + 5 - 6)
        {
            IgnoreLayout = true,
            Left = (BLOCK_WIDTH + 2) * index,
            Style = new Style { BackgroundColor = color, BorderRadius = 4 }
        };
    }

    private static float ChargingGradientOffset() =>
        -(float)(Environment.TickCount64 % 6000 / 6000.0);

    private BoxNode BuildPopup(BatterySnapshot battery) => new()
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            ModulesCommon.BuildTextWithIcon(theme, Icons.Info, "Battery"),
            BuildRow("Device", battery.Device),
            new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
            {
                new TextNode("Capacity", theme.Text, theme.Text),
                ModulesCommon.BuildTextWithIcon(theme, BatteryLevelIcon(battery.Percentage), $"{battery.Percentage}%"),
            },
            BuildRow("Status", battery.Status),
            ..BuildChargeLimitControl(battery.ChargeLimit),
            ..BuildPowerProfileSection(battery.PowerProfiles),
        ],
    };

    private IEnumerable<Node> BuildChargeLimitControl(int? chargeLimit)
    {
        if (chargeLimit is not { } limit)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(theme.Border, height: 16);
        yield return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 24 },
            Children =
            [
                new TextNode("Charge limit", theme.Text, theme.Text),
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
                            Children = [new TextNode($"{limit}%", theme.Text, theme.Text)],
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
        var state = buttonState.UpdateColor(theme.Text.MutedColor);
        return new BoxNode()
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = buttonState.Hovered,
            OnClick = enabled
                ? () => service.SetChargeLimit(service.Snapshot.ChargeLimit.GetValueOrDefault() + delta)
                : null,
            Style = ModulesCommon.ModuleStyle(theme, enabled ? state.Background : theme.Panel) with
            {
                Padding = 6,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(label, 14, enabled ? theme.Text : theme.Text.MutedColor)],
        };
    }

    private IEnumerable<Node> BuildPowerProfileSection(PowerProfileSnapshot powerProfiles)
    {
        if (!powerProfiles.Available)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(theme.Border, height: 16);
        yield return new TextNode("Power profile", theme.Text, theme.Text);
        yield return new BoxNode
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            Children =
            [
                ..powerProfiles.Profiles.Select((profile, i) =>
                    BuildPowerProfileButton(profile, powerProfiles.Active, i))
            ],
        };
    }

    private BoxNode BuildPowerProfileButton(string profile, string activeProfile, int index)
    {
        var active = profile.Equals(activeProfile, StringComparison.Ordinal);
        var normal = active ? theme.Active : theme.Panel;
        var state = _profileStates.GetState(profile, normal).UpdateColor(normal);

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = active ? null : () => service.SetPowerProfile(profile),
            Style = ModulesCommon.ModuleStyle(theme, state.Background, index == 0, index == 2) with
            {
                BorderWidth = active ? theme.Border.Width : 0,
                Padding = new Insets(7, 6),
            },
            Children = [new ImageNode(ProfileLabel(profile), 16, 16, theme.Text)],
        };
    }

    private static SvgAsset ProfileLabel(string profile) => profile switch
    {
        "power-saver" => Icons.Leaf,
        "balanced" => Icons.Scale,
        "performance" => Icons.Flame,
        _ => Icons.Scale,
    };

    private BoxNode BuildRow(string label, string value) =>
        new(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
        {
            new TextNode(label, theme.Text, theme.Text),
            new TextNode(value, theme.Text, theme.Text),
        };

    public static SvgAsset BatteryLevelIcon(int percentage) => Icons.BatteryLevels[percentage switch
    {
        <= 10 => 0,
        <= 35 => 1,
        <= 70 => 2,
        _ => 3,
    }];
}

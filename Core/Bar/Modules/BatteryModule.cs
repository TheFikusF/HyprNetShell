using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class BatteryModule(
    BatteryModuleService service,
    Theme theme) : IDrawableModule
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _profileStates = [];
    private readonly ModulesCommon.BoxState _chargeLimitDecreaseState = new();
    private readonly ModulesCommon.BoxState _chargeLimitIncreaseState = new();

    private readonly ModulesCommon.NodeWithPopup _node = new("battery_module")
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

    private Node BuildStateModule(BatterySnapshot battery)
    {
        var (left, right) = BatteryGradient(battery.Percentage);
        var icon = battery.IsCharging
            ? Icons.BatteryCharging
            : battery.IsCritical
                ? Icons.BatteryWarning
                : BatteryLevelIcon(battery.Percentage);

        return new GradientBoxNode(left, right, static () => 0.0f, 80)
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { Spacing = 8 },
            Children =
            [
                new ImageNode(icon, 18, 18, theme.Text),
                new TextNode($"{battery.Percentage}%", theme.TextSize, theme.Text),
            ],
        };
    }

    private BoxNode BuildPopup(BatterySnapshot battery) => new()
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            BuildRow("Device", battery.Device),
            BuildRow("Capacity", $"{battery.Percentage}%"),
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
                new TextNode("Charge limit", theme.TextSize, theme.Text),
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
                            Children = [new TextNode($"{limit}%", theme.TextSize, theme.Text)],
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
        var state = buttonState.UpdateColor(theme.Muted);
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
            Children = [new TextNode(label, 14, enabled ? theme.Text : theme.Muted)],
        };
    }

    private IEnumerable<Node> BuildPowerProfileSection(PowerProfileSnapshot powerProfiles)
    {
        if (!powerProfiles.Available)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(theme.Border, height: 16);
        yield return new TextNode("Power profile", theme.TextSize, theme.Text);
        yield return new BoxNode
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            Children = [..powerProfiles.Profiles.Select((profile, i) => BuildPowerProfileButton(profile, powerProfiles.Active, i))],
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
                BorderWidth = active ? theme.BorderWidth : 0,
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
            new TextNode(label, theme.TextSize, theme.Text),
            new TextNode(value, theme.TextSize, theme.Text),
        };

    private static (Color Left, Color Right) BatteryGradient(int percentage)
    {
        var red = Color.FromRgb(231, 76, 60, 0.92f);
        var green = Color.FromRgb(46, 204, 113, 0.92f);
        var charge = Math.Clamp(percentage, 0, 100);
        return charge < 50
            ? (Color.Lerp(red, green, charge / 50.0f), red)
            : (green, Color.Lerp(red, green, (charge - 50) / 50.0f));
    }

    public static SvgAsset BatteryLevelIcon(int percentage) => Icons.BatteryLevels[percentage switch
    {
        <= 10 => 0,
        <= 35 => 1,
        <= 70 => 2,
        _ => 3,
    }];
}

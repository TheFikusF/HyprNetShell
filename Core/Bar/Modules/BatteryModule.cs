using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class BatteryModule(
    Func<BatterySnapshot> snapshot,
    Theme theme) : IDrawableModule
{
    private readonly ModulesCommon.NodeWithPopup _node = new("battery_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var battery = snapshot();
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
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            BuildRow("Device", battery.Device),
            BuildRow("Capacity", $"{battery.Percentage}%"),
            BuildRow("Status", battery.Status),
        ],
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
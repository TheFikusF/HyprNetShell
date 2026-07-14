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

        return new GradientBoxNode(left, right, static () => 0.0f)
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { Spacing = 6 },
            Children =
            [
                new ImageNode(icon, 18, 18, theme.Text),
                new TextNode($"{battery.Percentage}%", 14.0f, theme.Text),
            ],
        };
    }

    private BoxNode BuildPopup(BatterySnapshot battery) => new ()
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            BuildPopupRow("Device", battery.Device),
            BuildPopupRow("Capacity", $"{battery.Percentage}%"),
            BuildPopupRow("Status", battery.Status),
        ],
    };

    private Node BuildPopupRow(string label, string value) =>
        new BoxNode
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 8 },
            Children =
            [
                new TextNode(label, 14.0f, theme.Text),
                new TextNode(value, 14.0f, theme.Text),
            ],
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

    public static SvgAsset BatteryLevelIcon(int percentage) => Icons.BatteryLevels[
        percentage switch
        {
            <= 10 => 0,
            <= 35 => 1,
            <= 70 => 2,
            _ => 3,
        }
    ];
}
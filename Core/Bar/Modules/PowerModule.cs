using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class PowerModule(Theme theme) : IDrawableModule
{
    private readonly RefBool _powerOffHovered = new();
    private readonly RefBool _rebootHovered = new();

    private readonly ModulesCommon.NodeWithPopup _node = new("power_module")
    {
        HorizontalAlignment = ItemsAlignment.End,
    };

    public Node Draw() => _node.Draw([
            new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Center,
                Style =
                    ModulesCommon.ModuleStyle(theme, ModulesCommon.ToBackground(theme, Color.FromRgb(210, 55, 55))) with
                    {
                        BorderRadius = 12,
                        Padding = 8
                    },
                Children = [new ImageNode(Icons.Power, 18, 18, theme.Text)],
            },
        ],
        BuildPopup);

    private Node BuildPopup() => new BoxNode(260)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            BuildAction("Power off", Icons.PowerOff, Color.FromRgb(210, 55, 55), _powerOffHovered, "poweroff"),
            BuildAction("Reboot", Icons.Reboot, Color.FromRgb(230, 145, 45), _rebootHovered, "reboot"),
        ],
    };

    private Node BuildAction(string label, SvgAsset icon, Color accent, RefBool hovered, string command)
    {
        var background = ModulesCommon.ToBackground(theme, accent);
        if (hovered)
        {
            background = Color.Lighten(background, 0.12f);
        }

        return new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = hovered,
            OnClick = () => RunSystemctl(command),
            Style = ModulesCommon.ModuleStyle(theme, background) with
            {
                BorderWidth = 0,
                BorderRadius = 8,
                Spacing = 10,
            },
            Children =
            [
                new ImageNode(icon, 20, 20, theme.Text),
                new TextNode(label, 14, theme.Text),
            ],
        };
    }

    private static void RunSystemctl(string command)
    {
        _ = CommandRunner.TryRunAsync(
            "systemctl",
            [command],
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
    }
}
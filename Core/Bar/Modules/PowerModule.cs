using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class PowerModule(
    DialogService dialogs,
    IHyprctl hyprctl,
    Action requestLockScreen,
    Theme theme,
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private readonly Ref<bool> _settingsHovered = new();
    private readonly Ref<bool> _lockHovered = new();
    private readonly Ref<bool> _logoutHovered = new();
    private readonly Ref<bool> _powerOffHovered = new();
    private readonly Ref<bool> _rebootHovered = new();

    private readonly NodeWithPopup _node = new(popupCoordinator, "power_module")
    {
        HorizontalAlignment = ItemsAlignment.End,
    };

    public Node Draw() => _node.Draw([
            new BoxNode(height: 52 - (int)(theme.Border.Width * 2))
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
            BuildAction("Settings", Icons.Settings, Color.FromRgb(95, 120, 190), _settingsHovered, OpenSettings),
            BuildAction("Lock screen", Icons.Lock, Color.FromRgb(70, 125, 210), _lockHovered, LockScreen),
            BuildAction("Log out", Icons.LogOut, Color.FromRgb(185, 95, 195), _logoutHovered, Logout),
            BuildAction("Power off", Icons.PowerOff, Color.FromRgb(210, 55, 55), _powerOffHovered,
                () => RunSystemctl("poweroff")),
            BuildAction("Reboot", Icons.Reboot, Color.FromRgb(230, 145, 45), _rebootHovered,
                () => RunSystemctl("reboot")),
        ],
    };

    private BoxNode BuildAction(string label, SvgAsset icon, Color accent, Ref<bool> hovered, Action onClick)
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
            OnClick = onClick,
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

    private void OpenSettings()
    {
        _node.ClosePopup();
        dialogs.Open<SettingsDialog>();
    }

    private void LockScreen()
    {
        _node.ClosePopup();
        requestLockScreen();
    }

    private void Logout()
    {
        _node.ClosePopup();
        _ = hyprctl.ExitSessionAsync();
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

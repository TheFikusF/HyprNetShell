using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class WorkspacesModule : IDrawableModule
{
    private readonly Dictionary<int, ModulesCommon.BoxState> _popupWorkspaceStates = [];
    private readonly Theme _theme;
    private readonly ModulesCommon.NodeWithPopup _node;
    private readonly HyprlandService _hyprland;
    private readonly IHyprctl _hyprctl;

    public WorkspacesModule(HyprlandService hyprland, IHyprctl hyprctl, SuperKeyStateService superKey, Theme theme,
        Func<bool> blockPopup)
    {
        _theme = theme;
        _hyprland = hyprland;
        _hyprctl = hyprctl;
        var blockPopup1 = blockPopup;
        _node = new("workspaces_module", ignorePopupQueue: true)
        {
            GetShouldShowPopup = hovered => (superKey.IsHeldFor(TimeSpan.FromMilliseconds(500)) || hovered) &&
                                            blockPopup1() == false,
        };
    }

    public Node Draw()
    {
        var snapshot = _hyprland.Snapshot;
        if (_node.ShouldShowPopup == false)
        {
            ResetPopupHoverState();
        }

        var title = string.IsNullOrWhiteSpace(snapshot.FocusedTitle) ? "Desktop" : snapshot.FocusedTitle;
        var className = string.IsNullOrWhiteSpace(snapshot.FocusedClassName) ? "APP" : snapshot.FocusedClassName;

        return _node.Draw([
            new BoxNode
            {
                Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with { BorderRadius = 8 },
                Children = [new TextNode(snapshot.FocusedWorkspaceId.ToString(), 24, _theme.Text)]
            },
            new BoxNode
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Center,
                Style = new Style
                {
                    BackgroundColor = Color.FromRgb(0, 0, 0, 0.9f),
                    Spacing = 4,
                    BorderColor = _theme.Border,
                    BorderRadius = new BorderRadius(0, _theme.BorderRadius, _theme.BorderRadius, 0),
                    Padding = new Insets(8, 8)
                },
                Children =
                {
                    ModulesCommon.BuildAppBadge(className, 14, _theme.Muted, _theme),
                    new TextNode(title.Length > 40 ? title[..37] + "..." : title, 14.0f, _theme.Text),
                },
            }
        ], () => BuildWorkspacePopup(snapshot, _theme));
    }

    private BoxNode BuildWorkspacePopup(HyprlandSnapshot snapshot, Theme theme) => new(400)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            ModulesCommon.BuildTextWithIcon(theme, Icons.Monitor, $"Monitor {snapshot.MonitorWorkspaces[0].Name}"),
            ..snapshot.Workspaces.Select(x => WorkspaceModule(x, theme))
        ]
    };

    private BoxNode WorkspaceModule(WorkspaceSnapshot workspace, Theme theme)
    {
        var state = _popupWorkspaceStates.GetState(workspace.Id, theme.Panel)
            .UpdateColor(workspace.Active ? theme.Active : theme.Panel);
        return new BoxNode
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => _ = _hyprctl.FocusWorkspaceAsync(workspace.Id),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Spacing = 8,
                BorderRadius = 8,
                BorderWidth = workspace.Active ? _theme.BorderWidth : 0,
            },
            Children =
            [
                new TextNode($"Workspace {workspace.Id}", theme.TextSize, theme.Text),
                ..workspace.Windows.Select(x => new BoxNode
                {
                    Style = new Style { Spacing = 8, Padding = new Insets(0, 0, 0, 4) },
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    Children =
                    [
                        ModulesCommon.BuildAppBadge(x.ClassName, 14, theme.Muted, theme),
                        new TextNode(x.Title, theme.TextSize, theme.Text, wrapping: TextWrapping.Ellipsis)
                    ]
                }),
            ],
        };
    }

    private void ResetPopupHoverState()
    {
        foreach (var state in _popupWorkspaceStates.Values)
        {
            state.Hovered.Value = false;
        }
    }
}
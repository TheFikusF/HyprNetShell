using System.Runtime.CompilerServices;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal sealed class WorkspacesModule : IDrawableModule
{
    private readonly Dictionary<int, ModulesCommon.BoxState> _popupWorkspaceStates = [];
    private readonly StatusBarTheme _theme;
    private readonly Func<bool> _blockPopup;
    private readonly ModulesCommon.NodeWithPopup _node;
    private HyprlandService _hyprland;
    private SuperKeyStateService _superKey;

    public WorkspacesModule(HyprlandService hyprland, SuperKeyStateService superKey, StatusBarTheme theme,
        Func<bool> blockPopup)
    {
        _theme = theme;
        _hyprland = hyprland;
        _superKey = superKey;
        _blockPopup = blockPopup;
        _node = new("workspaces_module", ignorePopupQueue: true)
        {
            GetShouldShowPopup = hovered => (_superKey.IsHeldFor(TimeSpan.FromMilliseconds(500)) || hovered) &&
                                         _blockPopup() == false,
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
                    BorderRadius = new BorderRadius(0, _theme.Radius, _theme.Radius, 0),
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

    private BoxNode BuildWorkspacePopup(HyprlandSnapshot snapshot, StatusBarTheme theme) =>
        new(400)
        {
            IgnoreLayout = true,
            Style = new Style
            {
                Padding = new Insets(36, 0, 0, 0),
            },
            Children =
            [
                new BoxNode(400)
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Start,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Style = new Style
                    {
                        BackgroundColor = Color.FromRgb(0, 0, 0, 0.9f),
                        BorderRadius = 8,
                        BorderColor = theme.Border,
                        BorderWidth = 2,
                        Padding = 8,
                        Spacing = 8
                    },
                    Children =
                    [
                        ..snapshot.Workspaces.Select<WorkspaceSnapshot, Node>(x => WorkspaceModule(x, theme))
                    ]
                }
            ]
        };

    private Node WorkspaceModule(WorkspaceSnapshot workspace, StatusBarTheme theme)
    {
        var normal = workspace.Active ? theme.Active : theme.Panel;
        var state = GetPopupWorkspaceState(workspace.Id, normal);
        var target = state.Hovered ? Color.Lighten(normal, workspace.Active ? 0.18f : 0.12f) : normal;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, ModulesCommon.DELTA_TIME);

        return new BoxNode
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => Task.Run(async () =>
                await HyprlandService.HyprctlEvalAsync(
                    $$"""hl.dispatch(hl.dsp.focus({ workspace = {{workspace.Id}} }))""", CancellationToken.None)),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Spacing = 4,
                BorderRadius = 8,
                BorderWidth = workspace.Active ? _theme.BorderWidth : 0,
            },
            Children =
            [
                new TextNode($"Workspace {workspace.Id}", 14.0f, theme.Text),
                ..workspace.Windows
                    .Select(x => new BoxNode
                    {
                        Style = new Style { Spacing = 8 },
                        Children =
                        [
                            ModulesCommon.BuildAppBadge(x.ClassName, 14, theme.Muted, theme),
                            new TextNode(x.Title.Length > 45 ? x.Title[..42] + "..." : x.Title, 14, theme.Text)
                        ]
                    }),
            ],
        };
    }

    private ModulesCommon.BoxState GetPopupWorkspaceState(int workspaceId, Color initialColor)
    {
        if (_popupWorkspaceStates.TryGetValue(workspaceId, out var state))
        {
            return state;
        }

        state = new ModulesCommon.BoxState(initialColor);
        _popupWorkspaceStates[workspaceId] = state;
        return state;
    }

    private void ResetPopupHoverState()
    {
        foreach (var state in _popupWorkspaceStates.Values)
        {
            state.Hovered.Value = false;
        }
    }
}
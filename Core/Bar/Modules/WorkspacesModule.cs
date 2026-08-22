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
    private readonly Func<string> _getOutputName;

    public WorkspacesModule(
        HyprlandService hyprland,
        IHyprctl hyprctl,
        SuperKeyStateService superKey,
        Theme theme,
        Func<string> getOutputName,
        Func<bool> blockPopup,
        ModulesCommon.PopupCoordinator popupCoordinator)
    {
        _theme = theme;
        _hyprland = hyprland;
        _hyprctl = hyprctl;
        _getOutputName = getOutputName;
        var blockPopup1 = blockPopup;
        _node = new(popupCoordinator, "workspaces_module", ignorePopupQueue: true)
        {
            TopOffset = 36,
            GetShouldShowPopup = hovered => (superKey.IsHeldFor(TimeSpan.FromMilliseconds(500)) || hovered) &&
                                            blockPopup1() == false,
        };
    }

    public Node Draw()
    {
        var snapshot = _hyprland.Snapshot;
        var monitor = ResolveMonitor(snapshot);
        if (_node.ShouldShowPopup == false)
        {
            ResetPopupHoverState();
        }

        var activeWorkspaceId = monitor?.ActiveWorkspaceId > 0
            ? monitor.ActiveWorkspaceId
            : 0;
        var activeWorkspace = monitor?.Workspaces.FirstOrDefault(workspace => workspace.Id == activeWorkspaceId);
        var representativeWindow = activeWorkspace?.Windows.FirstOrDefault();
        var useFocusedWindow = monitor?.Current == true;
        var title = useFocusedWindow ? snapshot.FocusedTitle : representativeWindow?.Title;
        var className = useFocusedWindow ? snapshot.FocusedClassName : representativeWindow?.ClassName;
        title = string.IsNullOrWhiteSpace(title) ? "Desktop" : title;
        className = string.IsNullOrWhiteSpace(className) ? "APP" : className;

        return _node.Draw([
            new BoxNode(height: 52 - (int)(_theme.BorderWidth * 2))
            {
                VerticalAlignment = ItemsAlignment.Center,
                OnScroll = delta => ScrollWorkspace(monitor, activeWorkspaceId, delta),
                Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with { BorderRadius = 8 },
                Children = [new TextNode(activeWorkspaceId > 0 ? activeWorkspaceId.ToString() : "?", 24, _theme.Text)]
            },
            new BoxNode
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Center,
                OnScroll = delta => ScrollWorkspace(monitor, activeWorkspaceId, delta),
                Style = new Style
                {
                    BackgroundColor = Color.FromRgb(0, 0, 0, 0.9f),
                    Spacing = 4,
                    BorderColor = _theme.Border,
                    BorderRadius = new BorderRadius(0, _theme.BorderRadius, _theme.BorderRadius, 0),
                    Padding = new Insets(8, 8),
                    ShadowColor = Color.Black with { A = 0.45f },
                    ShadowDistance = 5.0f
                },
                Children =
                {
                    ModulesCommon.BuildAppBadge(className, 14, _theme.Muted, _theme),
                    new TextNode(title.Length > 40 ? title[..37] + "..." : title, 14.0f, _theme.Text),
                },
            }
        ], () => BuildWorkspacePopup(snapshot, _theme));
    }

    private MonitorWorkspaceSnapshot? ResolveMonitor(HyprlandSnapshot snapshot) =>
        snapshot.MonitorWorkspaces.FirstOrDefault(monitor =>
            string.Equals(monitor.Name, _getOutputName(), StringComparison.Ordinal));

    private void ScrollWorkspace(
        MonitorWorkspaceSnapshot? monitor,
        int activeWorkspaceId,
        float scrollDelta)
    {
        var workspaces = monitor
            ?.Workspaces
            .Select(workspace => workspace.Id)
            .Distinct()
            .Order()
            .ToArray() ?? [];

        if (workspaces.Length < 2)
        {
            return;
        }

        var currentIndex = Array.IndexOf(workspaces, activeWorkspaceId);
        if (currentIndex < 0)
        {
            currentIndex = 0;
        }

        var direction = scrollDelta > 0.0f ? 1 : -1;
        var targetIndex = (currentIndex + direction + workspaces.Length) % workspaces.Length;
        _ = _hyprctl.FocusWorkspaceAsync(workspaces[targetIndex]);
    }

    private BoxNode BuildWorkspacePopup(HyprlandSnapshot snapshot, Theme theme)
    {
        var outputName = _getOutputName();
        var monitors = snapshot.MonitorWorkspaces
            .OrderBy(monitor => string.Equals(monitor.Name, outputName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(monitor => monitor.Name, StringComparer.Ordinal)
            .ToArray();

        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Start,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.PopupStyle(theme) with { Spacing = 8 },
            Children = monitors.Length == 0
                ? [BuildMonitorColumn(outputName, [], theme)]
                : [..monitors.Select(monitor => BuildMonitorColumn(monitor.Name, monitor.Workspaces, theme))],
        };
    }

    private BoxNode BuildMonitorColumn(
        string monitorName,
        IReadOnlyList<WorkspaceSnapshot> workspaces,
        Theme theme) => new(400)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 8 },
        Children =
        [
            ModulesCommon.BuildTextWithIcon(theme, Icons.Monitor, $"Monitor {monitorName}"),
            ..workspaces.Select(workspace => WorkspaceModule(workspace, theme)),
        ],
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

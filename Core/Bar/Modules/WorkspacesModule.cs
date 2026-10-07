using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
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

    private readonly NodeWithPopup _node;
    private readonly HyprlandService _hyprland;
    private readonly IHyprctl _hyprctl;
    private readonly Func<string> _getOutputName;

    public WorkspacesModule(
        HyprlandService hyprland,
        IHyprctl hyprctl,

        Func<string> getOutputName,
        Func<bool> blockPopup,
        PopupCoordinator popupCoordinator)
    {
        _hyprland = hyprland;
        _hyprctl = hyprctl;
        _getOutputName = getOutputName;
        var blockPopup1 = blockPopup;
        _node = new(popupCoordinator, "workspaces_module", ignorePopupQueue: true) {
            TopOffset = 36,
            GetShouldShowPopup = hovered => hovered &&
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
            new BoxNode(height: 52 - (int)(ThemeManager.Current.Border.Width * 2))
            {
                VerticalAlignment = ItemsAlignment.Center,
                OnScroll = delta => ScrollWorkspace(monitor, activeWorkspaceId, delta),
                Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with { BorderRadius = 8 },
                Children = [new TextNode(activeWorkspaceId > 0 ? activeWorkspaceId.ToString() : "?", 24)]
            },
            new BoxNode
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Center,
                OnScroll = delta => ScrollWorkspace(monitor, activeWorkspaceId, delta),
                Style = new Style
                {
                    BackgroundColor = Color.FromRgb(0, 0, 0, 0.9f),
                    Spacing = 8,
                    BorderRadius = new BorderRadius(0, ThemeManager.Current.Border.Radius, ThemeManager.Current.Border.Radius, 0),
                    Padding = new Insets(8, 12, 8, 8),
                    ShadowColor = Color.Black with { A = 0.45f },
                    ShadowDistance = 4.0f
                },
                Children =
                {
                    ModulesCommon.BuildAppBadge(className, ThemeManager.Current.IconSize, ThemeManager.Current.Text.MutedColor),
                    new TextNode(title.Length > 40 ? title[..37] + "..." : title),
                },
            }
        ], () => BuildWorkspacePopup(snapshot));
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

    private BoxNode BuildWorkspacePopup(HyprlandSnapshot snapshot)
    {
        var outputName = _getOutputName();
        var monitors = snapshot.MonitorWorkspaces
            .OrderBy(monitor => string.Equals(monitor.Name, outputName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(monitor => monitor.Name, StringComparer.Ordinal)
            .ToArray();

        return new BoxNode {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Start,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.PopupStyle() with { Spacing = 8 },
            Children = monitors.Length == 0
                ? [BuildMonitorColumn(outputName, [])]
                : [.. monitors.Select(monitor => BuildMonitorColumn(monitor.Name, monitor.Workspaces))],
        };
    }

    private BoxNode BuildMonitorColumn(string monitorName, IReadOnlyList<WorkspaceSnapshot> workspaces) => new(400) {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children =
        [
            ModulesCommon.BuildTextWithIcon(Icons.Monitor, $"Monitor {monitorName}"),
            ..workspaces.Select(WorkspaceModule),
        ],
    };

    private BoxNode WorkspaceModule(WorkspaceSnapshot workspace)
    {
        var state = _popupWorkspaceStates.GetState(workspace.Id, ThemeManager.Current.Panel)
            .UpdateColor(workspace.Active ? ThemeManager.Current.Active : ThemeManager.Current.Panel);
        return new BoxNode {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => _ = _hyprctl.FocusWorkspaceAsync(workspace.Id),
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Spacing = 8,
                BorderRadius = 8,
                BorderWidth = workspace.Active ? ThemeManager.Current.Border.Width : 0,
            },
            Children =
            [
                new TextNode($"Workspace {workspace.Id}"),
                ..workspace.Windows.Select(x => new BoxNode(new Style { Spacing = 8, Padding = new Insets(0, 0, 0, 4) },
                    horizontalAlignment: ItemsAlignment.Stretch,
                    verticalAlignment: ItemsAlignment.Center)
                {
                    ModulesCommon.BuildAppBadge(x.ClassName, ThemeManager.Current.IconSize, ThemeManager.Current.Text.MutedColor),
                    new TextNode(x.Title, wrapping: TextWrapping.Ellipsis)
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

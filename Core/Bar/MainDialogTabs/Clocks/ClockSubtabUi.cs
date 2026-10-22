using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs.Clocks;

internal sealed class ClockSubtabUi
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];

    public BoxNode Button(string title, Action action)
    {
        var color = ThemeManager.Current.Panel;
        var actionKey = title is "Pause" or "Resume" or "Start / resume" ? "run" : title;
        var state = _buttonStates.GetState(actionKey, color).UpdateColor(color);
        return new BoxNode {
            OnClick = action,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Padding = 12,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(title)],
        };
    }

    public static Node Build(params Node[] children) => new BoxNode(new Style { Spacing = 12 }) {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Children = children,
    };
}

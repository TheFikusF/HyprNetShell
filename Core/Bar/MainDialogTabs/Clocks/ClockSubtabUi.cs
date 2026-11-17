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

internal static class ClockSubtabUi
{
    public static ActionButton.ButtonStyle ButtonStyle => ActionButton.ButtonStyle.Default with {
        Padding = 12,
    };

    public static Node Build(params Node[] children) => new BoxNode(new Style { Spacing = 12 }) {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Children = children,
    };
}

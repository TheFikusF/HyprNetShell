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

internal static class ClockFormatting
{
    internal static string FormatDuration(TimeSpan duration) =>
    $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";

    internal static string FormatOffset(DateTime time, DateTime local)
    {
        var offset = time - local;
        var minutes = (int)Math.Round(offset.TotalMinutes);
        return $"{(minutes >= 0 ? "+" : "-")}{Math.Abs(minutes) / 60}:{Math.Abs(minutes) % 60:00}";
    }
}

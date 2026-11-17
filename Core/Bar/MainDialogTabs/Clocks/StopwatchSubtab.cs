using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs.Clocks.Stopwatch;

internal sealed class StopwatchSubtab : IClocksSubtab
{
    private readonly Dictionary<string, ActionButton> _buttons = [];
    private readonly TextInputCoordinator _inputs;

    public StopwatchSubtab(TextInputCoordinator inputs)
    {
        _inputs = inputs;
    }

    public void Activate() => _inputs.Deactivate();

    public Node Draw()
    {
        var tools = ClockToolsService.Shared;
        return ClockSubtabUi.Build(
            new TextNode(ClockFormatting.FormatDuration(tools.StopwatchElapsed), 32),
            ActionButton.Build(_buttons, "run", tools.StopwatchRunning ? "Pause" : "Start / resume", () =>
            {
                if (tools.StopwatchRunning)
                {
                    tools.PauseStopwatch();
                }
                else
                {
                    tools.StartStopwatch();
                }
            }, style: ClockSubtabUi.ButtonStyle),
            ActionButton.Build(_buttons, "Reset", "Reset", tools.ResetStopwatch, style: ClockSubtabUi.ButtonStyle),
            new TextNode(""));
    }
}

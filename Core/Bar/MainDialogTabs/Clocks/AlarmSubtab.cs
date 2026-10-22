using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs.Clocks.Alarm;

internal sealed class AlarmSubtab : IClocksSubtab
{
    private readonly ClockSubtabUi _ui = new();
    private readonly TextInputCoordinator _inputs;
    private readonly TextInputCoordinator.Input _alarmInput;
    private string _message = "";

    public AlarmSubtab(TextInputCoordinator inputs)
    {
        _inputs = inputs;
        _alarmInput = inputs.Create("Local alarm (HH:mm)", "", "07:00", 5);
    }

    public void Activate()
    {
        _message = "";
        _inputs.Activate(_alarmInput);
    }

    public Node Draw()
    {
        var tools = ClockToolsService.Shared;
        return ClockSubtabUi.Build([
            _inputs.Build(_alarmInput),
            _ui.Button("Add alarm", () =>
            {
                if (TimeOnly.TryParseExact(_alarmInput.Value, "HH:mm", out var time))
                {
                    tools.AddAlarm(time);
                    _message = "Alarm set for the next local occurrence.";
                }
                else
                {
                    _message = "Enter a valid local time as HH:mm.";
                }
            }),
            ..tools.Alarms.Select(alarm => _ui.Button(
                $"{alarm.NextOccurrence:ddd dd MMM HH:mm} · Remove", () => tools.RemoveAlarm(alarm.Id))),
            new TextNode(_message)]);
    }
}

using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs.Clocks.Timer;

internal sealed class TimerSubtab : IClocksSubtab
{
    private readonly ClockSubtabUi _ui = new();
    private readonly ModulesCommon.BoxState _timerDisplayState = new();
    private readonly TextInputCoordinator _inputs;
    private readonly TextInputCoordinator.Input _timerInput;
    private string _message = "";

    public TimerSubtab(TextInputCoordinator inputs)
    {
        _inputs = inputs;
        _timerInput = inputs.Create("", "", "", 6,
            transform: value => new string(value.Where(char.IsAsciiDigit).ToArray()), alwaysActive: true);
    }

    public void Activate()
    {
        _message = "";
        if (ClockToolsService.Shared.TimerRemaining is not null)
        {
            _inputs.Deactivate();
        }
        else
        {
            _inputs.Activate(_timerInput);
        }
    }

    public void ActivateSelection() => StartTimer();

    public Node Draw()
    {
        var tools = ClockToolsService.Shared;
        List<Node> children =
            [BuildTimerDisplay(tools),
            new TextNode("Type up to six digits · HH:MM:SS · Enter to start", color: ThemeManager.Current.Text.MutedColor),
            _ui.Button("Start new timer", StartTimer),
            ..tools.TimerRemaining is not null ? [
                _ui.Button(tools.TimerRunning ? "Pause" : "Resume", () =>
                {
                    if (tools.TimerRunning)
                    {
                        tools.PauseTimer();
                    }
                    else
                    {
                        tools.ResumeTimer();
                    }
                }),
                _ui.Button("Reset", () =>
                {
                    tools.ResetTimer();
                    _inputs.SetValue(_timerInput, "");
                    _inputs.Activate(_timerInput);
                })] : ((List<Node>)[]) ];

        children.Add(new TextNode(_message));
        return ClockSubtabUi.Build(children.ToArray());
    }

    private string TimerEntry()
    {
        var digits = _timerInput.Value.PadLeft(6, '0');
        return $"{digits[..2]}:{digits[2..4]}:{digits[4..]}";
    }

    private void StartTimer()
    {
        var digits = _timerInput.Value.PadLeft(6, '0');
        var hours = int.Parse(digits[..2]);
        var minutes = int.Parse(digits[2..4]);
        var seconds = int.Parse(digits[4..]);
        var duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        if (minutes < 60 && seconds < 60 && duration > TimeSpan.Zero)
        {
            ClockToolsService.Shared.StartTimer(duration);
            _inputs.Deactivate();
            _message = "";
        }
        else
        {
            _message = "Enter a positive duration with minutes and seconds between 00 and 59.";
        }
    }

    private Node BuildTimerDisplay(ClockToolsService tools)
    {
        var editing = _inputs.IsActive(_timerInput);
        var remaining = tools.TimerRemaining;
        var text = editing || remaining is null ? TimerEntry() : ClockFormatting.FormatDuration(remaining.Value);
        var state = _timerDisplayState.UpdateColor(ThemeManager.Current.Panel);
        return new BoxNode(height: 180) {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                if (!_inputs.IsActive(_timerInput))
                {
                    _inputs.SetValue(_timerInput, "");
                }

                _inputs.Activate(_timerInput);
            },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Padding = 24,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(text, 48)],
        };
    }
}

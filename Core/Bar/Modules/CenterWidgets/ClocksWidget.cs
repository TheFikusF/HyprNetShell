using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;

using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class ClocksWidget
{
    public const int WIDTH = 280;

    private readonly ModulesCommon.BoxState _titleState = new();
    private readonly WorldClockService _clocks;
    private readonly ClipboardHistoryService _clipboard;
    private readonly Dictionary<string, ModulesCommon.BoxState> _dateCopyButtons = new();

    public ClocksWidget(ClipboardHistoryService clipboard)
        : this(WorldClockService.Shared, clipboard)
    {
    }

    public ClocksWidget(WorldClockService clocks,
        ClipboardHistoryService clipboard)
    {
        _clocks = clocks;
        _clipboard = clipboard;
    }

    public Node Draw(DateTime now, Action? openClocks = null)
    {
        var tools = ClockToolsService.Shared;
        var timer = tools.TimerRemaining;
        if (timer is not null || tools.StopwatchActive)
        {
            return new BoxNode(WIDTH) {
                Direction = Direction.Vertical,
                Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with { BorderRadius = 8, Spacing = 8 },
                Children =
                [
                    ModulesCommon.CentralWidgetHeader(Icons.Clock, "Clocks", openClocks, _titleState),
                        new TextNode(timer is not null ? "Timer" : "Stopwatch"),
                        new TextNode(MainDialogTabs.Clocks.ClockFormatting.FormatDuration(timer ?? tools.StopwatchElapsed), 28),
                        new TextNode((timer is not null ? tools.TimerRunning : tools.StopwatchRunning) ? "Running" : "Paused"),
                    ],
            };
        }

        return new BoxNode(WIDTH) {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with {
                BorderRadius = 8,
                Spacing = 8,
            },
            Children =
    [
        ModulesCommon.CentralWidgetHeader(Icons.Clock, "Clocks", openClocks, _titleState),
            BuildRow("Local", now),
            .. _clocks.SelectedClocks.Select(clock =>
                BuildRow(clock.DisplayName, WorldClockService.GetTime(clock, now.ToUniversalTime()), now)),
        ],
        };
    }

    private BoxNode BuildRow(string label, DateTime time, DateTime? local = null)
    {
        var state = _dateCopyButtons.GetState(label, ThemeManager.Current.Panel).UpdateColor(ThemeManager.Current.Panel);
        return new BoxNode(Style.Empty, ItemsAlignment.Spread, ItemsAlignment.Center)
        {
            new TextNode(label),
            new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
            {
                new TextNode(local is null ? time.ToString("HH:mm") : $"{time:HH:mm} ({MainDialogTabs.Clocks.ClockFormatting.FormatOffset(time, local.Value)})"),
                new BoxNode
                {
                    IsHovered = state.Hovered,
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    OnClick = () => _ = _clipboard.CopyTextAsync($"{label} - {time:HH:mm}"),
                    Style = ModulesCommon.ModuleStyle(state.Background) with
                    {
                        Padding = 4,
                        BorderRadius = 8,
                        BorderWidth = 0,
                    },
                    Children = [new ImageNode(Icons.Copy, 14, 14, ThemeManager.Current.Text)]
                }
            }
        };
    }

}

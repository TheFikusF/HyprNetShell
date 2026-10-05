using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class TodaysEventsWidget(CalendarService calendar)
{
    public const int WIDTH = 540;
    private const int VisibleEventCount = 5;

    private readonly ModulesCommon.BoxState _titleState = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _eventStates = [];
    private int _firstEventIndex;

    internal Node Draw(DateTime now, Action openCalendar)
    {
        var events = CalendarTab.EventsOn(calendar.Snapshot.Occurrences, DateOnly.FromDateTime(now));
        BoundedListUi.NormalizeViewport(ref _firstEventIndex, events.Count, VisibleEventCount);

        return new BoxNode(WIDTH)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with
            {
                BorderRadius = 8,
                Spacing = 8,
            },
            Children =
            [
                ModulesCommon.CentralWidgetHeader(Icons.Calendar, "Today's events", openCalendar, _titleState),
                ..BuildEvents(events),
            ],
        };
    }

    private IEnumerable<Node> BuildEvents(IReadOnlyList<CalendarOccurrence> events)
    {
        if (events.Count == 0)
        {
            yield return new BoxNode(height: 80)
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Children = [new TextNode("Nothing scheduled for today", ThemeManager.Current.Text.HeaderSize, ThemeManager.Current.Text.MutedColor)],
            };
            yield break;
        }

        yield return new BoxNode(Style.Empty, ItemsAlignment.End)
        {
            new TextNode($"{events.Count} event{(events.Count == 1 ? "" : "s")}", color: ThemeManager.Current.Text.MutedColor),
        };

        yield return CalendarTab.BuildEventsList(events, _firstEventIndex, VisibleEventCount, _eventStates, delta => Scroll(delta, events.Count));
    }

    private void Scroll(float delta, int eventCount) => BoundedListUi.MoveViewport(
        ref _firstEventIndex, delta > 0 ? 1 : -1,
        eventCount, VisibleEventCount);
}

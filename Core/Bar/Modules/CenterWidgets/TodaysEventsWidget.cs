using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class TodaysEventsWidget(CalendarService calendar, Theme theme)
{
    public const int WIDTH = 540;
    private const int VisibleEventCount = 5;

    private readonly ModulesCommon.BoxState _titleState = new();
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
            OnScroll = delta => Scroll(delta, events.Count),
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 8,
            },
            Children =
            [
                ModulesCommon.CentralWidgetHeader(Icons.Calendar, "Today's events", openCalendar, _titleState, theme),
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
                Children = [new TextNode("Nothing scheduled for today", theme.Text.HeaderSize, theme.Text.MutedColor)],
            };
            yield break;
        }

        yield return new BoxNode(Style.Empty, ItemsAlignment.End)
        {
            new TextNode($"{events.Count} event{(events.Count == 1 ? "" : "s")}", theme.Text, theme.Text.MutedColor),
        };

        foreach (var item in events.VisibleItems(_firstEventIndex, VisibleEventCount))
        {
            yield return BuildEvent(item.Item);
        }
    }

    private BoxNode BuildEvent(CalendarOccurrence occurrence)
    {
        var time = occurrence.IsAllDay
            ? "All day"
            : occurrence.End > occurrence.Start
                ? $"{occurrence.Start:HH:mm}–{occurrence.End:HH:mm}"
                : occurrence.Start.ToString("HH:mm");
        return new BoxNode(height: occurrence.Location is { Length: > 0 } ? 52 : 40)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style
            {
                BackgroundColor = Color.Lighten(theme.Panel, 0.08f),
                BorderRadius = 8,
                Padding = new Insets(10, 5),
                Spacing = 3,
            },
            Children =
            [
                new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
                {
                    new TextNode(occurrence.Title, theme.Text.HeaderSize, theme.Text, 380, TextWrapping.Ellipsis),
                    new TextNode(time, theme.Text, theme.Text.MutedColor),
                },
                ..(occurrence.Location is { Length: > 0 } location
                    ? new Node[] { new TextNode(location, theme.Text.SmallSize, theme.Text.MutedColor, 490, TextWrapping.Ellipsis) }
                    : []),
            ],
        };
    }

    private void Scroll(float delta, int eventCount) => BoundedListUi.MoveViewport(
        ref _firstEventIndex,
        delta > 0 ? 1 : -1,
        eventCount,
        VisibleEventCount);
}

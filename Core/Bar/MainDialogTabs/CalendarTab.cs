using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class CalendarTab : IMainDialogTab
{
    private const int VisibleEventCount = 7;

    private readonly CalendarService _calendar;
    private readonly Theme _theme;
    private readonly CalendarWidget _month;
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);
    private int _firstEventIndex;

    internal CalendarTab(CalendarService calendar, Theme theme)
    {
        _calendar = calendar;
        _theme = theme;
        _month = new CalendarWidget(calendar, theme, 400);
    }

    public string Id => "calendar";
    public string Title => "Calendar";
    public SvgAsset Icon => Icons.Calendar;

    public void Activate()
    {
        _selectedDate = DateOnly.FromDateTime(DateTime.Today);
        _firstEventIndex = 0;
    }

    public void HandleTextInput(string text)
    {
    }

    public void HandleBackspace()
    {
    }

    public void MoveSelection(SelectionDirection direction)
    {
        _selectedDate = direction switch
        {
            SelectionDirection.Left => _selectedDate.AddDays(-1),
            SelectionDirection.Right => _selectedDate.AddDays(1),
            SelectionDirection.Up => _selectedDate.AddDays(-7),
            SelectionDirection.Down => _selectedDate.AddDays(7),
            _ => _selectedDate,
        };
        _month.ShowDate(_selectedDate);
        _firstEventIndex = 0;
    }

    public void ActivateSelection()
    {
    }

    public Node Draw()
    {
        var snapshot = _calendar.Snapshot;
        var events = EventsOn(snapshot.Occurrences, _selectedDate);
        BoundedListUi.NormalizeViewport(ref _firstEventIndex, events.Count, VisibleEventCount);

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 14 },
            Children =
            [
                BuildHeader(snapshot),
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Start,
                    Style = new Style { Spacing = 16 },
                    Children =
                    [
                        _month.Draw(DateTime.Now, selectedDate: _selectedDate, onDateSelected: SelectDate),
                        BuildEvents(events),
                    ],
                },
            ],
        };
    }

    private BoxNode BuildHeader(CalendarSnapshot snapshot)
    {
        var status = _calendar.IsRefreshing
            ? "Refreshing…"
            : snapshot.UpdatedAt is { } updated
                ? $"Updated {updated:g} · {_calendar.ConfiguredSourceCount} source{(_calendar.ConfiguredSourceCount == 1 ? "" : "s")}"
                : _calendar.ConfiguredSourceCount == 0 ? "Enable calendar sources in Settings" : "Not refreshed yet";

        return new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
        {
            MainDialogTabUi.BuildSectionHeader("Calendar", status),
            BuildButton(_calendar.IsRefreshing ? "Refreshing…" : "Refresh now", "refresh", Refresh,
                _calendar.IsRefreshing),
        };
    }

    private BoxNode BuildEvents(IReadOnlyList<CalendarOccurrence> events)
    {
        var content = events.Count == 0
            ? new BoxNode
            {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Stretch,
                Style = new Style { Spacing = 12 },
                Children =
                [
                    MainDialogTabUi.BuildSectionHeader(_selectedDate.ToString("dddd, MMMM d"), "No events"),
                    MainDialogTabUi.BuildMessage(_theme, "Nothing scheduled for this day."),
                ],
            }
            : new BoxNode
            {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Stretch,
                Style = Style.Spacer,
                Children =
                [
                    MainDialogTabUi.BuildSectionHeader(
                        _selectedDate.ToString("dddd, MMMM d"),
                        $"{events.Count} event{(events.Count == 1 ? "" : "s")}"),
                    ..events.VisibleItems(_firstEventIndex, VisibleEventCount)
                        .Select(item => BuildEvent(item.Item, item.Index)),
                ],
            };

        return new BoxNode(420)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            OnScroll = delta => ScrollEvents(delta, events.Count),
            Children =
            [
                events.Count > VisibleEventCount
                    ? BoundedListUi.BuildScrollableResults(
                        content,
                        _firstEventIndex,
                        events.Count,
                        VisibleEventCount,
                        _theme,
                        delta => ScrollEvents(delta, events.Count))
                    : content,
            ],
        };
    }

    private BoxNode BuildEvent(CalendarOccurrence occurrence, int index)
    {
        var state = _buttonStates.GetState("event-" + index, _theme.Panel).UpdateColor(_theme.Panel);
        var time = occurrence.IsAllDay
            ? "All day"
            : occurrence.End > occurrence.Start
                ? $"{occurrence.Start:HH:mm}–{occurrence.End:HH:mm}"
                : occurrence.Start.ToString("HH:mm");

        return new BoxNode(height: occurrence.Location is null ? 62 : 78)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
            {
                Padding = new Insets(14, 8),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 4,
            },
            Children =
            [
                new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
                {
                    new TextNode(occurrence.Title, _theme.Text.HeaderSize, _theme.Text, maxWidth: 275, wrapping: TextWrapping.Ellipsis),
                    new TextNode(time, _theme.Text, _theme.Text.MutedColor),
                },
                ..(occurrence.Location is { Length: > 0 } location
                    ? new Node[] { new TextNode(location, _theme.Text, _theme.Text.MutedColor, maxWidth: 380,
                        wrapping: TextWrapping.Ellipsis) }
                    : []),
            ],
        };
    }

    private BoxNode BuildButton(string label, string key, Action action, bool disabled)
    {
        var state = _buttonStates.GetState(key, _theme.Panel).UpdateColor(_theme.Panel);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = disabled ? null : state.Hovered,
            OnClick = disabled ? null : action,
            Opacity = disabled ? 0.5f : 1,
            Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
            {
                Padding = new Insets(12, 8),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children =
            [
                new ImageNode(Icons.Reboot, 16, 16, _theme.Text),
                new TextNode(label, _theme.Text, _theme.Text),
            ],
        };
    }

    private void SelectDate(DateOnly date)
    {
        _selectedDate = date;
        _firstEventIndex = 0;
    }

    private void ScrollEvents(float delta, int eventCount)
    {
        BoundedListUi.MoveViewport(
            ref _firstEventIndex,
            delta > 0 ? 1 : -1,
            eventCount,
            VisibleEventCount);
    }

    private void Refresh() => _ = _calendar.ForceRefreshAsync();

    internal static IReadOnlyList<CalendarOccurrence> EventsOn(
        IEnumerable<CalendarOccurrence> occurrences,
        DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
        var dayEnd = dayStart.AddDays(1);
        return occurrences
            .Where(occurrence => occurrence.Start < dayEnd &&
                (occurrence.End > dayStart || occurrence.End == occurrence.Start && occurrence.Start >= dayStart))
            .OrderBy(occurrence => occurrence.IsAllDay ? 0 : 1)
            .ThenBy(occurrence => occurrence.Start)
            .ThenBy(occurrence => occurrence.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

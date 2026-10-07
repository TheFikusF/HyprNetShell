using HyprNetShell.GUI;
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
    private const int VISIBLE_EVENT_COUNT = 7;

    private readonly CalendarService _calendar;

    private readonly CalendarWidget _month;
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private DateOnly _selectedDate = DateOnly.FromDateTime(DateTime.Today);
    private int _firstEventIndex;

    public string Id => "calendar";
    public string Title => "Calendar";
    public SvgAsset Icon => Icons.Calendar;

    public bool HandleScroll => false;

    internal CalendarTab(CalendarService calendar)
    {
        _calendar = calendar;

        _month = new CalendarWidget(calendar, 400);
    }

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
        _selectedDate = direction switch {
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
        BoundedListUi.NormalizeViewport(ref _firstEventIndex, events.Count, VISIBLE_EVENT_COUNT);

        return new BoxNode {
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

    private BoxNode BuildEvents(IReadOnlyList<CalendarOccurrence> events) => new(420) {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children = [
            MainDialogTabUi.BuildSectionHeader(_selectedDate.ToString("dddd, MMMM d"),
                events.Count == 0 ? "No events" : $"{events.Count} event{(events.Count == 1 ? "" : "s")}"),

            BuildEventsList(events, _firstEventIndex, VISIBLE_EVENT_COUNT, _buttonStates, delta => ScrollEvents(delta, events.Count))
        ],
    };

    internal static Node BuildEventsList(
        IReadOnlyList<CalendarOccurrence> events,
        int firstIndex,
        int visibleEvents, Dictionary<string, ModulesCommon.BoxState> buttonStates,
        Action<float> onScroll) => events.Count == 0
            ? MainDialogTabUi.BuildMessage("Nothing scheduled for this day.")
            : BoundedListUi.BuildList(events,
                (occurrence, index) => BuildEvent(occurrence, index, buttonStates),
                firstIndex, visibleEvents, onScroll);

    private static BoxNode BuildEvent(
        CalendarOccurrence occurrence,
        int index, Dictionary<string, ModulesCommon.BoxState> buttonStates)
    {
        var state = buttonStates.GetState("event-" + index, ThemeManager.Current.Panel).UpdateColor(ThemeManager.Current.Panel);
        var time = occurrence.IsAllDay
            ? "All day"
            : occurrence.End > occurrence.Start
                ? $"{occurrence.Start:HH:mm}–{occurrence.End:HH:mm}"
                : occurrence.Start.ToString("HH:mm");

        return new BoxNode() {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Padding = new Insets(12, 8),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 4,
            },
            Children =
            [
                new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
                {
                    new TextNode(occurrence.Title, ThemeManager.Current.Text.HeaderSize, maxWidth: 275, maxLines: 3, wrapping: TextWrapping.Wrap),
                    new TextNode(time, color: ThemeManager.Current.Text.MutedColor),
                },
                ..(occurrence.Location is { Length: > 0 } location
                    ? new Node[] { new TextNode(location, color: ThemeManager.Current.Text.MutedColor, maxWidth: 380, wrapping: TextWrapping.Ellipsis) }
                    : []),
            ],
        };
    }

    private BoxNode BuildButton(string label, string key, Action action, bool disabled)
    {
        var state = _buttonStates.GetState(key, ThemeManager.Current.Panel).UpdateColor(ThemeManager.Current.Panel);
        return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = disabled ? null : state.Hovered,
            OnClick = disabled ? null : action,
            Opacity = disabled ? 0.5f : 1,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Padding = new Insets(12, 8),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children =
            [
                new ImageNode(Icons.Reboot, 16, 16, ThemeManager.Current.Text),
                new TextNode(label),
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
            VISIBLE_EVENT_COUNT);
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

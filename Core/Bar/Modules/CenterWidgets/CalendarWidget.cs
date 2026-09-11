using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class CalendarWidget
{
    public const int WIDTH = 360;

    private readonly CalendarService _calendar;
    private readonly Theme _theme;
    private readonly int _width;
    private readonly int _cellWidth;
    private readonly ModulesCommon.BoxState _previousMonthState = new();
    private readonly ModulesCommon.BoxState _nextMonthState = new();
    private readonly ModulesCommon.BoxState _titleState = new();
    private readonly Dictionary<DateOnly, ModulesCommon.BoxState> _dayStates = [];
    private DateTime? _displayedMonth;
    private DateTime? _lastCurrentMonth;

    internal CalendarWidget(CalendarService calendar, Theme theme, int width = WIDTH)
    {
        _calendar = calendar;
        _theme = theme;
        _width = width;
        _cellWidth = width > WIDTH ? 48 : 42;
    }

    public Node Draw(
        DateTime now,
        Action? openCalendar = null,
        DateOnly? selectedDate = null,
        Action<DateOnly>? onDateSelected = null,
        bool showTooltips = false)
    {
        var currentMonth = new DateTime(now.Year, now.Month, 1);
        if (_displayedMonth is null || _displayedMonth == _lastCurrentMonth)
        {
            _displayedMonth = currentMonth;
        }

        _lastCurrentMonth = currentMonth;
        var first = _displayedMonth.Value;
        var days = DateTime.DaysInMonth(first.Year, first.Month);
        var offset = ((int)first.DayOfWeek + 6) % 7;
        var occurrences = _calendar.Snapshot.Occurrences;

        return new BoxNode(_width)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 10,
            },
            Children =
            [
                BuildMonthHeader(first, openCalendar),
                BuildWeekHeader(),
                ..BuildWeeks(first, offset, days, DateOnly.FromDateTime(now), selectedDate, onDateSelected,
                    occurrences, showTooltips),
            ],
        };
    }

    internal void ShowDate(DateOnly date) => _displayedMonth = new DateTime(date.Year, date.Month, 1);

    private Node BuildMonthHeader(DateTime month, Action? openCalendar)
    {
        var titleState = _titleState.UpdateColor(_theme.Panel);
        return new BoxNode(Style.Spacer, ItemsAlignment.Stretch, ItemsAlignment.Center)
        {
            BuildMonthButton(Icons.ChevronLeft, -1, _previousMonthState),
            new BoxNode(height: 34)
            {
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Center,
                OnClick = openCalendar,
                IsHovered = openCalendar is null ? null : titleState.Hovered,
                Style = ModulesCommon.ModuleStyle(_theme, titleState.Background) with
                {
                    Padding = new Insets(10, 0),
                    BorderRadius = 8,
                    BorderWidth = 0,
                    Spacing = 8,
                },
                Children =
                [
                    new ImageNode(Icons.Calendar, 22, 22, _theme.Text),
                    new TextNode(month.ToString("MMMM yyyy"), 22, _theme.Text),
                ],
            },
            BuildMonthButton(Icons.ChevronRight, 1, _nextMonthState),
        };
    }

    private Node BuildMonthButton(SvgAsset icon, int monthDelta, ModulesCommon.BoxState buttonState)
    {
        var state = buttonState.UpdateColor(_theme.Panel);
        return new BoxNode(34, 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => _displayedMonth = (_displayedMonth ?? DateTime.Today).AddMonths(monthDelta),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new ImageNode(icon, 20, 20, _theme.Text)],
        };
    }

    private BoxNode BuildWeekHeader() => new(Style.Spacer)
    {
        Children = ((string[])["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"])
            .Select(BuildLabelCell)
            .ToArray(),
    };

    private IEnumerable<Node> BuildWeeks(
        DateTime first,
        int offset,
        int days,
        DateOnly today,
        DateOnly? selectedDate,
        Action<DateOnly>? onDateSelected,
        IReadOnlyList<CalendarOccurrence> occurrences,
        bool showTooltips)
    {
        var cells = Enumerable.Repeat(0, offset).Concat(Enumerable.Range(1, days)).ToArray();
        foreach (var week in cells.Chunk(7))
        {
            yield return new BoxNode
            {
                Style = Style.Spacer,
                Children =
                [
                    ..week
                        .Concat(Enumerable.Repeat(0, 7 - week.Length))
                        .Select(day => day == 0
                            ? BuildLabelCell("")
                            : BuildDayCell(
                                new DateOnly(first.Year, first.Month, day),
                                today,
                                selectedDate,
                                onDateSelected,
                                occurrences,
                                showTooltips)),
                ],
            };
        }
    }

    private BoxNode BuildDayCell(
        DateOnly date,
        DateOnly today,
        DateOnly? selectedDate,
        Action<DateOnly>? onDateSelected,
        IReadOnlyList<CalendarOccurrence> occurrences,
        bool showTooltips)
    {
        var events = CalendarTab.EventsOn(occurrences, date);
        var active = date == today;
        var selected = date == selectedDate;
        var state = _dayStates.GetState(date, _theme.Panel).UpdateColor(selected ? _theme.Active : _theme.Panel);
        var children = new List<Node>
        {
            new TextNode(date.Day.ToString(), 16, _theme.Text),
        };
        if (events.Count > 0)
        {
            children.Add(new BoxNode(new Style { Spacing = 2 }, ItemsAlignment.Center, ItemsAlignment.Center)
            {
                Children = [.. Enumerable.Range(0, Math.Min(3, events.Count)).Select(_ => BuildDot())],
            });
        }

        if (showTooltips && events.Count > 0 && state.Hovered.Value)
        {
            children.Add(new LayeredNode(() => BuildTooltip(date, events), RenderLayer.OptionsSelector, _cellWidth - 2, -8));
        }

        return new BoxNode(_cellWidth, 34)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = onDateSelected is null ? null : () => onDateSelected(date),
            Style = new Style
            {
                BackgroundColor = selected
                    ? state.Background
                    : active
                        ? _theme.Active
                        : state.Hovered.Value ? state.Background : null,
                BorderColor = selected ? _theme.Border : null,
                BorderWidth = selected ? _theme.Border.Width : 0,
                BorderRadius = active || selected || state.Hovered.Value ? 8 : 0,
                Spacing = 1,
            },
            Children = children,
        };
    }

    private BoxNode BuildLabelCell(string text) => new(_cellWidth, 30)
    {
        HorizontalAlignment = ItemsAlignment.Center,
        VerticalAlignment = ItemsAlignment.Center,
        Children = [new TextNode(text, 16, _theme.Text)],
    };

    private BoxNode BuildDot() => new(4, 4)
    {
        Style = new Style { BackgroundColor = _theme.Active, BorderRadius = 999 },
    };

    private Node BuildTooltip(DateOnly date, IReadOnlyList<CalendarOccurrence> events) => new BoxNode(280)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(_theme) with { Padding = 12, Spacing = 6 },
        Children =
        [
            new TextNode(date.ToString("dddd, MMMM d"), 15, _theme.Text),
            ..events.Take(5).Select(occurrence => new TextNode(
                occurrence.IsAllDay ? $"All day · {occurrence.Title}" : $"{occurrence.Start:HH:mm} · {occurrence.Title}",
                13,
                _theme.Text,
                maxWidth: 256,
                wrapping: TextWrapping.Ellipsis)),
            ..(events.Count > 5
                ? new Node[] { new TextNode($"+{events.Count - 5} more", 12, _theme.Text.MutedColor) }
                : []),
        ],
    };
}

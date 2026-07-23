using HyprNetShell.Core.Assets;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class CalendarWidget(Theme theme)
{
    public const int WIDTH = 360;

    private readonly ModulesCommon.BoxState _previousMonthState = new();
    private readonly ModulesCommon.BoxState _nextMonthState = new();
    private DateTime? _displayedMonth;
    private DateTime? _lastCurrentMonth;

    public Node Draw(DateTime now)
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
        var today = first == currentMonth ? now.Day : 0;

        return new BoxNode(WIDTH)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 10,
            },
            Children =
            [
                BuildMonthHeader(first),
                BuildWeekHeader(),
                ..BuildWeeks(offset, days, today),
            ],
        };
    }

    private Node BuildMonthHeader(DateTime month) => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            BuildMonthButton(Icons.ChevronLeft, -1, _previousMonthState),
            new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
            {
                new ImageNode(Icons.Calendar, 22, 22, theme.Text),
                new TextNode(month.ToString("MMMM yyyy"), 22, theme.Text),
            },
            BuildMonthButton(Icons.ChevronRight, 1, _nextMonthState),
        ],
    };

    private Node BuildMonthButton(
        Rendering.SvgAsset icon,
        int monthDelta,
        ModulesCommon.BoxState buttonState)
    {
        var state = buttonState.UpdateColor(theme.Panel);
        return new BoxNode(34, 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => _displayedMonth = (_displayedMonth ?? DateTime.Today).AddMonths(monthDelta),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new ImageNode(icon, 20, 20, theme.Text)],
        };
    }

    private BoxNode BuildWeekHeader() => new (Style.Spacer)
    {
        Children = ((string[])["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"])
            .Select(day => BuildCell(day, false))
            .ToArray(),
    };

    private IEnumerable<Node> BuildWeeks(int offset, int days, int today)
    {
        var cells = Enumerable.Repeat(-1, offset).Concat(Enumerable.Range(1, days)).ToArray();
        foreach (var week in cells.Chunk(7))
        {
            yield return new BoxNode
            {
                Style = Style.Spacer,
                Children =
                [
                    ..week
                        .Concat(Enumerable.Repeat(-1, 7 - week.Length))
                        .Select(day => BuildCell(day == -1 ? "" : day.ToString(), day == today)),
                ],
            };
        }
    }

    private BoxNode BuildCell(string text, bool active) => new (42, 30)
    {
        HorizontalAlignment = ItemsAlignment.Center,
        VerticalAlignment = ItemsAlignment.Center,
        Style = new Style
        {
            BackgroundColor = active ? theme.Active : null,
            BorderRadius = active ? 8 : 0,
        },
        Children = [new TextNode(text, 16, theme.Text)],
    };
}

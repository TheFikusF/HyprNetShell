using HyprNetShell.Core.Assets;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class CalendarWidget(Theme theme)
{
    public const int WIDTH = 360;
    
    public Node Draw(DateTime now)
    {
        var first = new DateTime(now.Year, now.Month, 1);
        var days = DateTime.DaysInMonth(now.Year, now.Month);
        var offset = ((int)first.DayOfWeek + 6) % 7;

        return new BoxNode(WIDTH)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 10,
            },
            Children =
            [
                new BoxNode
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 8 },
                    Children =
                    [
                        new ImageNode(Icons.Calendar, 22, 22, theme.Text),
                        new TextNode(now.ToString("MMMM yyyy"), 22, theme.Text)
                    ]
                },
                BuildWeekHeader(),
                ..BuildWeeks(offset, days, now.Day),
            ],
        };
    }

    private Node BuildWeekHeader() => new BoxNode
    {
        Style = new Style { Spacing = 8 },
        Children =
        [
            ..new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }
                .Select(day => BuildCell(day, false)),
        ],
    };

    private IEnumerable<Node> BuildWeeks(int offset, int days, int today)
    {
        var cells = Enumerable.Repeat(0, offset).Concat(Enumerable.Range(1, days)).ToArray();
        foreach (var week in cells.Chunk(7))
        {
            yield return new BoxNode
            {
                Style = new Style { Spacing = 8 },
                Children =
                [
                    ..week
                        .Concat(Enumerable.Repeat(0, 7 - week.Length))
                        .Select(day => BuildCell(day == 0 ? "" : day.ToString(), day == today)),
                ],
            };
        }
    }

    private Node BuildCell(string text, bool active) => new BoxNode(42, 30)
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
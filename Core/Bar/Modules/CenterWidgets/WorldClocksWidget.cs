using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class WorldClocksWidget(Theme theme)
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _dateCopyButtons = new();

    public Node Draw(DateTime now) => new BoxNode(220)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
        {
            BorderRadius = 8,
            Spacing = 8,
        },
        Children =
        [
            new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Center,
                Style = new Style { Spacing = 8 },
                Children =
                [
                    new ImageNode(Icons.Clock, 22, 22, theme.Text),
                    new TextNode("World clocks", 22, theme.Text)
                ]
            },
            BuildRow("Local", now),
            BuildRow("UTC", DateTime.UtcNow),
            BuildRow("Kyiv", ConvertUtc("Europe/Kyiv")),
            BuildRow("Tel-Aviv", ConvertUtc("Asia/Tel_Aviv")),
            BuildRow("New York", ConvertUtc("America/New_York")),
            BuildRow("San Francisco", ConvertUtc("America/Los_Angeles")),
            BuildRow("Tokyo", ConvertUtc("Asia/Tokyo")),
        ],
    };

    private Node BuildRow(string label, DateTime time)
    {
        var state = _dateCopyButtons.GetState(label, theme.Panel);
        var target = state.Hovered
            ? Color.Lighten(theme.Panel, 0.12f)
            : theme.Panel;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, ModulesCommon.DELTA_TIME);

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Children =
            [
                new TextNode(label, theme.TextSize, theme.Text),
                new BoxNode
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 8 },
                    Children =
                    [
                        new TextNode(time.ToString("HH:mm"), theme.TextSize, theme.Text),
                        new BoxNode
                        {
                            IsHovered = state.Hovered,
                            OnClick = () => Utils.CopyToClipboard($"{label} - {time:HH:mm}"),
                            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
                            {
                                Padding = 4,
                                BorderRadius = 8,
                            },
                            Children = [new ImageNode(Icons.Copy, 14, 14, theme.Text)]
                        }
                    ]
                }
            ],
        };
    }

    private static DateTime ConvertUtc(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTime.UtcNow, timeZoneId);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}
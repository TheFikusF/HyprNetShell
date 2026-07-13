using System.Diagnostics;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class CenterModule(
    Func<NotificationsSnapshot> notifications,
    Theme theme) : IDrawableModule
{
    private const int POPUP_WIDTH = 620;

    private readonly AppIconResolver _iconResolver = new();

    private readonly ModulesCommon.NodeWithPopup _node = new("center_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var now = DateTime.Now;
        var snapshot = notifications();

        return _node.Draw([
            BuildDateBadge(now),
            BuildTimeWidget(now),
            BuildNotificationsBadge(snapshot),
        ], () => BuildPopup(now, snapshot));
    }

    private Node BuildDateBadge(DateTime now) =>
        new BoxNode
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel, right: false),
            Children =
            [
                new TextNode(now.ToString("ddd dd, MMM"), 14.0f, theme.Text),
            ],
        };

    private static float GradientOffset() => -(float)(Environment.TickCount64 % 4600 / 4600.0);

    private Node BuildTimeWidget(DateTime now) =>
        new GradientBoxNode(TimeBlue(now), Color.FromRgb(255, 214, 66), GradientOffset, 96)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
            OnClick = () => Process.Start(new ProcessStartInfo
            {
                FileName = "gnome-clocks",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            }),
            Children =
            [
                new TextNode(now.ToString("HH:mm"), 24, theme.Text)
                {
                    ShadowColor = Color.FromRgb(0, 0, 0, 0.7f),
                    ShadowDistance = 2,
                },
                new BoxNode
                {
                    IgnoreLayout = true,
                    Style = new Style
                    {
                        Padding = new Insets(6, 0, 0, -92)
                    },
                    Children =
                    [
                        new BoxNode(12, 12)
                        {
                            Style = ModulesCommon.ModuleStyle(theme, Color.Orange) with
                            {
                                Padding = new Insets(16, -16, 0, -16)
                            },
                        }
                    ]
                }
            ]
        };

    private static Color TimeBlue(DateTime now)
    {
        var hour = now.TimeOfDay.TotalHours;
        var dayAmount = MathF.Max(0.0f, MathF.Sin((float)((hour - 6.0) / 12.0 * Math.PI)));
        return LerpColor(Color.FromRgb(49, 2, 110), Color.FromRgb(92, 191, 255), dayAmount);
    }

    private static Color LerpColor(Color a, Color b, float t) =>
        new(
            a.R + (b.R - a.R) * t,
            a.G + (b.G - a.G) * t,
            a.B + (b.B - a.B) * t,
            a.A + (b.A - a.A) * t);

    private Node BuildNotificationsBadge(NotificationsSnapshot snapshot) =>
        new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = OpenSwayncPanel,
            Style = ModulesCommon.ModuleStyle(theme,
                snapshot.Count > 0 ? Color.Lerp(theme.Active, theme.Panel, 0.5f) : theme.Panel, left: false),
            Children =
            [
                new TextNode($"🔔 {snapshot.Count}", 14.0f, theme.Text),
                new BoxNode
                {
                    IgnoreLayout = true,
                    Style = new Style
                    {
                        Padding = new Insets(1, 0, 0, -16)
                    },
                    Children =
                    [
                        new BoxNode(12, 12)
                        {
                            Style = ModulesCommon.ModuleStyle(theme, Color.Orange) with
                            {
                                Padding = new Insets(16, -16, 0, -16)
                            },
                        }
                    ]
                }
            ],
        };

    private Node BuildPopup(DateTime now, NotificationsSnapshot snapshot) =>
        new BoxNode(POPUP_WIDTH)
        {
            IgnoreLayout = true,
            Style = new Style
            {
                Padding = new Insets(36, 0, 0, 0),
            },
            Children =
            [
                new BoxNode(POPUP_WIDTH)
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Start,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    Style = new Style
                    {
                        BackgroundColor = Color.FromRgb(0, 0, 0, 0.92f),
                        BorderColor = theme.Border,
                        BorderRadius = 8,
                        BorderWidth = 2,
                        Padding = 12,
                        Spacing = 12
                    },
                    Children =
                    [
                        new BoxNode
                        {
                            Direction = Direction.Horizontal,
                            VerticalAlignment = ItemsAlignment.Start,
                            Style = new Style { Spacing = 12 },
                            Children =
                            [
                                BuildCalendar(now),
                                new BoxNode
                                {
                                    Direction = Direction.Vertical,
                                    VerticalAlignment = ItemsAlignment.Start,
                                    Style = new Style { Spacing = 12 },
                                    Children =
                                    [
                                        BuildWorldClocks(now),
                                        BuildWeatherPlaceholder(),
                                    ]
                                }
                            ]
                        },
                        ModulesCommon.BuildDivider(theme.Border, height: 12),
                        BuildNotificationsList(snapshot)
                    ]
                }
            ]
        };

    private Node BuildCalendar(DateTime now)
    {
        var first = new DateTime(now.Year, now.Month, 1);
        var days = DateTime.DaysInMonth(now.Year, now.Month);
        var offset = ((int)first.DayOfWeek + 6) % 7;

        return new BoxNode(360)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 10
            },
            Children =
            [
                new TextNode(now.ToString("MMMM yyyy"), 22.0f, theme.Text),
                BuildCalendarWeekHeader(),
                ..BuildCalendarWeeks(offset, days, now.Day),
            ]
        };
    }

    private Node BuildCalendarWeekHeader() =>
        new BoxNode
        {
            Direction = Direction.Horizontal,
            Style = new Style { Spacing = 8 },
            Children =
            [
                ..new[] { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" }
                    .Select(day => BuildCalendarCell(day, false, theme.Text))
            ]
        };

    private IEnumerable<Node> BuildCalendarWeeks(int offset, int days, int today)
    {
        var cells = Enumerable.Repeat(0, offset)
            .Concat(Enumerable.Range(1, days))
            .ToArray();

        foreach (var week in cells.Chunk(7))
        {
            yield return new BoxNode
            {
                Direction = Direction.Horizontal,
                Style = new Style { Spacing = 8 },
                Children =
                [
                    ..week
                        .Concat(Enumerable.Repeat(0, 7 - week.Length))
                        .Select(day => BuildCalendarCell(day == 0 ? "" : day.ToString(), day == today, theme.Text))
                ]
            };
        }
    }

    private Node BuildCalendarCell(string text, bool active, Color color) =>
        new BoxNode(42, 30)
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style
            {
                BackgroundColor = active ? theme.Active : null,
                BorderRadius = active ? 8 : 0
            },
            Children =
            [
                new TextNode(text, 16.0f, color)
            ]
        };

    private Node BuildWorldClocks(DateTime now) =>
        new BoxNode(220)
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Start,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 8
            },
            Children =
            [
                new TextNode("World clocks", 16.0f, theme.Text),
                BuildClockRow("Local", now),
                BuildClockRow("UTC", DateTime.UtcNow),
                BuildClockRow("Kyiv", ConvertUtc("Europe/Kyiv")),
                BuildClockRow("New York", ConvertUtc("America/New_York")),
                BuildClockRow("Tokyo", ConvertUtc("Asia/Tokyo")),
            ]
        };

    private Node BuildClockRow(string label, DateTime time) =>
        new BoxNode(196)
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Children =
            [
                new TextNode(label, 14.0f, theme.Text),
                new TextNode(time.ToString("HH:mm"), 14.0f, theme.Text),
            ]
        };

    private Node BuildWeatherPlaceholder() =>
        new BoxNode(220)
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Start,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 8
            },
            Children =
            [
                new TextNode("Weather", 16.0f, theme.Text),
                new TextNode("⛅ --°C", 22.0f, theme.Text),
                new TextNode("Placeholder", 14.0f, theme.Text),
            ]
        };

    private Node BuildNotificationsList(NotificationsSnapshot snapshot) =>
        new BoxNode(596)
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Start,
            Style = new Style
            {
                Spacing = 8
            },
            Children =
            [
                new TextNode("Notifications", 16.0f, theme.Text),
                ..BuildNotificationRows(snapshot)
            ]
        };

    private IEnumerable<Node> BuildNotificationRows(NotificationsSnapshot snapshot)
    {
        if (snapshot.Items.Count == 0)
        {
            if (snapshot.Count == 0)
            {
                yield return new TextNode("No notifications", 14.0f, theme.Muted);
                yield break;
            }

            yield return new BoxNode
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Center,
                OnClick = OpenSwayncPanel,
                Style = new Style
                {
                    BackgroundColor = theme.Muted,
                    BorderRadius = 8,
                    Padding = new Insets(8, 6),
                },
                Children =
                [
                    new TextNode($"Open swaync notification center ({snapshot.Count})", 14.0f, theme.Text),
                ]
            };
            yield break;
        }

        foreach (var notification in snapshot.Items.Take(5))
        {
            var iconPath = string.IsNullOrWhiteSpace(notification.IconName)
                ? null
                : _iconResolver.TryResolve(notification.IconName);

            yield return new BoxNode
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Center,
                Style = new Style
                {
                    BackgroundColor = theme.Panel,
                    BorderRadius = 8,
                    Padding = new Insets(8, 6),
                    BorderWidth = 2,
                    BorderColor = theme.Border,
                    Spacing = 8
                },
                Children =
                [
                    ..BuildNotificationIcon(iconPath),
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        VerticalAlignment = ItemsAlignment.Start,
                        Style = new Style { Spacing = 4 },
                        Children =
                        [
                            new TextNode(Trim(notification.Title, 52), 14.0f, theme.Text),
                            new TextNode(Trim(notification.Body, 64), 13.0f, theme.Text),
                        ]
                    }
                ]
            };
        }
    }

    private static IEnumerable<Node> BuildNotificationIcon(string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            yield return new ImageNode(iconPath, 28, 28);
        }
    }

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";

    private static void OpenSwayncPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "swaync-client",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-op" },
            });
        }
        catch
        {
            // Ignore command failures; the notification badge will keep reflecting swaync state.
        }
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

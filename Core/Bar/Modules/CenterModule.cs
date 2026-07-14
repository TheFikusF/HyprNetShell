using System.Diagnostics;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class CenterModule : IDrawableModule
{
    private const int PopupWidth = 620;

    private readonly Func<NotificationsSnapshot> _notifications;
    private readonly Theme _theme;
    private readonly CalendarWidget _calendar;
    private readonly WorldClocksWidget _worldClocks;
    private readonly WeatherWidget _weather;
    private readonly NotificationsWidget _notificationsWidget;

    private readonly ModulesCommon.NodeWithPopup _node = new("center_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public CenterModule(Func<NotificationsSnapshot> notifications, Theme theme)
    {
        _notifications = notifications;
        _theme = theme;
        _calendar = new CalendarWidget(theme);
        _worldClocks = new WorldClocksWidget(theme);
        _weather = new WeatherWidget(theme);
        _notificationsWidget = new NotificationsWidget(theme);
    }

    public Node Draw()
    {
        var now = DateTime.Now;
        var snapshot = _notifications();

        return _node.Draw(
            [BuildDateBadge(now), BuildTimeWidget(now), BuildNotificationsBadge(snapshot)],
            () => BuildPopup(now, snapshot));
    }

    private Node BuildDateBadge(DateTime now) => new BoxNode
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel, right: false),
        Children = [new TextNode(now.ToString("ddd dd, MMM"), 14, _theme.Text)],
    };

    private Node BuildTimeWidget(DateTime now) =>
        new GradientBoxNode(TimeBlue(now), Color.FromRgb(255, 214, 66), GradientOffset, 96)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with { BorderRadius = 8 },
            OnClick = OpenClocks,
            Children =
            [
                new TextNode(now.ToString("HH:mm"), 24, _theme.Text)
                {
                    ShadowColor = Color.FromRgb(0, 0, 0, 0.7f),
                    ShadowDistance = 2,
                },
                BuildIndicator(-92, 6)
            ],
        };

    private Node BuildNotificationsBadge(NotificationsSnapshot snapshot) => new BoxNode
    {
        Direction = Direction.Horizontal,
        VerticalAlignment = ItemsAlignment.Center,
        OnClick = NotificationsWidget.OpenPanel,
        Style = ModulesCommon.ModuleStyle(
            _theme,
            snapshot.Count > 0 ? Color.Lerp(_theme.Active, _theme.Panel, 0.5f) : _theme.Panel,
            left: false),
        Children =
        [
            new TextNode($"🔔 {snapshot.Count}", 14, _theme.Text),
            BuildIndicator(-16, 1),
        ],
    };

    private Node BuildPopup(DateTime now, NotificationsSnapshot snapshot) => new BoxNode
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(_theme),
        Children =
        [
            new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Stretch,
                Style = new Style { Spacing = 12 },
                Children =
                [
                    _calendar.Draw(now),
                    _worldClocks.Draw(now), 
                    _weather.Draw()
                ],
            },
            ModulesCommon.BuildDivider(_theme.Border, height: 12),
            _notificationsWidget.Draw(snapshot),
        ],
    };

    private Node BuildIndicator(float leftPadding, float top) => new BoxNode
    {
        IgnoreLayout = true,
        Style = new Style { Padding = new Insets(top, 0, 0, leftPadding) },
        Children =
        [
            new BoxNode(12, 12)
            {
                Style = ModulesCommon.ModuleStyle(_theme, Color.Orange) with
                {
                    Padding = new Insets(16, -16, 0, -16),
                },
            },
        ],
    };

    private static float GradientOffset() => -(float)(Environment.TickCount64 % 4600 / 4600.0);

    private static Color TimeBlue(DateTime now)
    {
        var hour = now.TimeOfDay.TotalHours;
        var dayAmount = MathF.Max(0, MathF.Sin((float)((hour - 6) / 12 * Math.PI)));
        return Color.Lerp(Color.FromRgb(49, 2, 110), Color.FromRgb(92, 191, 255), dayAmount);
    }

    private static void OpenClocks()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "gnome-clocks",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
        }
        catch
        {
            // The clock application is optional.
        }
    }
}
using System.Diagnostics;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class CenterModule : IDrawableModule
{
    private const string CLOCK_IMAGE_RESOURCE_NAME = "HyprNetShell.Assets.Clock_3.png";
    private const string SUN_MOON_IMAGE_RESOURCE_NAME = "HyprNetShell.Assets.Clock_4.png";
    private static readonly EncodedImageData ClockImage = LoadClockImage(CLOCK_IMAGE_RESOURCE_NAME);
    private static readonly EncodedImageData SunMoonImage = LoadClockImage(SUN_MOON_IMAGE_RESOURCE_NAME);

    private readonly Func<NotificationsSnapshot> _notifications;
    private readonly Theme _theme;
    private readonly CalendarWidget _calendar;
    private readonly WorldClocksWidget _worldClocks;
    private readonly WeatherWidget _weather;
    private readonly NotificationsWidget _notificationsWidget;

    private float _clockRotation;

    private readonly ModulesCommon.NodeWithPopup _node = new("center_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public CenterModule(
        Func<NotificationsSnapshot> notifications,
        NotificationService notificationService,
        Theme theme)
    {
        _notifications = notifications;
        _theme = theme;
        _calendar = new CalendarWidget(theme);
        _worldClocks = new WorldClocksWidget(theme);
        _weather = new WeatherWidget(theme);
        _notificationsWidget = new NotificationsWidget(notificationService, theme);
    }

    public Node Draw()
    {
        var now = DateTime.Now;
        var snapshot = _notifications();

        return _node.Draw([
                new BoxNode(400 - 27 - 27)
                    { BuildDateBadge(now), BuildTimeWidget(now), BuildNotificationsBadge(snapshot) }
            ],
            () => BuildPopup(now, snapshot));
    }

    private Node BuildDateBadge(DateTime now) => new BoxNode
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel, right: false),
        Children = [new TextNode(now.ToString("ddd dd, MMM"), 14, _theme.Text)],
    };

    private Node BuildTimeWidget(DateTime now)
    {
        var targetRotation = _node.IsHovered ? DayRotation(now) + (float)Math.PI : DayRotation(now);
        _clockRotation = PrimitivesMath.LerpSmooth(_clockRotation, targetRotation, 9.0f, ModulesCommon.DELTA_TIME);
        return new BoxNode(148, 32)
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel, false, false) with
            {
                Padding = new Insets(6, 4),
                Spacing = 6,
            },
            OnClick = OpenClocks,
            Children =
            [
                new BoxNode
                {
                    Style = new Style { Padding = new Insets(-96, 0, 0, 0) },
                    IgnoreLayout = true,
                    Children =
                    [
                        new BoxNode(new Style
                            {
                                BorderColor = Color.White,
                                BorderWidth = _theme.BorderWidth,
                                BorderRadius = 999,
                                BackgroundColor = Color.Black
                            })
                            { new ImageNode(ClockImage, 160, 160) { RotationRadians = _clockRotation } }
                    ]
                },
                new BoxNode
                {
                    Style = new Style { Padding = new Insets(-96 - 10, 0, 0, 0) },
                    IgnoreLayout = true,
                    Children =
                    [
                        new ImageNode(SunMoonImage, 180, 180) { RotationRadians = _clockRotation }
                    ]
                },
                new BoxNode
                {
                    Style = new Style { Padding = new Insets(-32, 0, 0, 0) },
                    IgnoreLayout = true,
                    Children =
                    [
                        new TextNode(now.ToString("HH:mm"), 24, _theme.Text)
                        {
                            ShadowColor = Color.FromRgb(0, 0, 0, 0.7f),
                            ShadowDistance = 2,
                        },
                    ]
                },
            ],
        };
    }

    private Node BuildNotificationsBadge(NotificationsSnapshot snapshot) => new BoxNode
    {
        Direction = Direction.Horizontal,
        VerticalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel, left: false),
        Children = [new TextNode($"🔔 {snapshot.Count}", 14, _theme.Text)]
    };

    private Node BuildPopup(DateTime now, NotificationsSnapshot snapshot) => new BoxNode
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(_theme),
        Children =
        [
            new BoxNode(new Style { Spacing = 12 }, verticalAlignment: ItemsAlignment.Stretch)
            {
                _calendar.Draw(now), _worldClocks.Draw(now), _weather.Draw()
            },
            ModulesCommon.BuildDivider(_theme.Border, height: 12),
            _notificationsWidget.Draw(snapshot),
        ],
    };

    private static float GradientOffset() => (float)(Environment.TickCount64 % 4600 / 4600.0);

    private static float DayRotation(DateTime now) =>
        (float)((now.TimeOfDay.TotalDays * Math.Tau) - 0.5 * Math.PI);

    private static EncodedImageData LoadClockImage(string path)
    {
        using var stream = typeof(CenterModule).Assembly.GetManifestResourceStream(path)
            ?? throw new InvalidOperationException($"Embedded clock image '{path}' was not found.");
        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        return new EncodedImageData("image/png", buffer.ToArray());
    }

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

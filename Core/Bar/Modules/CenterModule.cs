using System.Diagnostics;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;
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

    private readonly NotificationService _notificationService;
    private readonly Theme _theme;
    private readonly CalendarWidget _calendar;
    private readonly WorldClocksWidget _worldClocks;
    private readonly WeatherWidget _weather;
    private readonly DialogService _dialogs;
    private readonly TabsService _tabs;
    private readonly NotificationsWidget _notificationsWidget;

    private float _clockRotation;
    private Rect? _clockBounds;

    private readonly NodeWithPopup _node;

    public CenterModule(
        NotificationService notificationService,
        WeatherWidget weather,
        DialogService dialogs,
        TabsService tabs,
        Theme theme,
        PopupCoordinator popupCoordinator)
    {
        _notificationService = notificationService;
        _theme = theme;
        _node = new(popupCoordinator, "center_module")
        {
            HorizontalAlignment = ItemsAlignment.Center,
        };
        _calendar = new CalendarWidget(theme);
        _worldClocks = new WorldClocksWidget(theme);
        _weather = weather;
        _dialogs = dialogs;
        _tabs = tabs;
        _notificationsWidget = new NotificationsWidget(notificationService, theme);
    }

    public Node Draw()
    {
        var now = DateTime.Now;
        var snapshot = _notificationService.Snapshot;

        return _node.Draw([
                new BoxNode(400 - 27 - 27)
                {
                    new BoxNode()
                    {
                        Style = new Style()
                        {
                            BorderRadius = 999,
                            ShadowColor = Color.Black with { A = 0.45f },
                            ShadowDistance = 5.0f
                        },
                        Children = [
                            BuildDateBadge(now),
                            new BoxNode(148, 36)
                            {
                                Direction = Direction.Horizontal,
                                HorizontalAlignment = ItemsAlignment.Center,
                                VerticalAlignment = ItemsAlignment.Center,
                                Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel, false, false) with
                                {
                                    Padding = new Insets(6, 4),
                                    ShadowColor = null,
                                    Spacing = 6,
                                }
                            },
                            BuildTimeWidget(now),
                            BuildNotificationsBadge(snapshot)
                        ]
                    }
                }
            ],
            () => BuildPopup(now, snapshot));
        // () => new SpacerNode());
    }

    private BoxNode BuildDateBadge(DateTime now) => new(height: 36)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel, right: false) with { ShadowColor = null },
        Children = [new TextNode(now.ToString(" ddd dd, MMM"), 14, _theme.Text)],
    };

    private BoxNode BuildTimeWidget(DateTime now)
    {
        const int CLOCK_SIZE = 160;
        const int SUN_SIZE = 48;
        const int SUN_ORBIT_RADIUS = 67;
        const int OVERLAY_CENTER_X = 80;
        const int OVERLAY_CENTER_Y = -38;
        var targetRotation = ClockTargetRotation(now);
        _clockRotation = PrimitivesMath.LerpSmooth(_clockRotation, targetRotation, 9.0f, Renderer.DeltaTime);
        return new BoxNode(CLOCK_SIZE + 6)
        {
            Left = (400 - 27 - 27) / 2 - (CLOCK_SIZE + 6) / 2,
            IgnoreLayout = true,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = OpenClocks,
            Children =
            [
                new BoxNode()
                {
                    IgnoreLayout = true,
                    Top = OVERLAY_CENTER_Y - CLOCK_SIZE / 2,
                    Children =
                    [
                        new BoxNode(new Style
                        {
                            BorderColor = Color.White,
                            BorderWidth = _theme.BorderWidth,
                            BorderRadius = 999,
                            BackgroundColor = Color.Black,
                            ShadowColor = Color.Black with { A = 0.45f },
                            ShadowDistance = 5.0f
                        })
                        {
                            new ImageNode(OtherAssets.ClockFace, CLOCK_SIZE, CLOCK_SIZE)
                                { RotationRadians = _clockRotation }
                        }
                    ]
                },
                new BoundsReportingBoxNode(CLOCK_SIZE + 6, CLOCK_SIZE + 6, bounds => _clockBounds = bounds)
                {
                    IgnoreLayout = true,
                    Top = OVERLAY_CENTER_Y - CLOCK_SIZE / 2,
                    Children =
                    [
                        new BoxNode(SUN_SIZE, SUN_SIZE)
                        {
                            HorizontalAlignment = ItemsAlignment.Center,
                            VerticalAlignment = ItemsAlignment.Center,
                            IgnoreLayout = true,
                            Left = (OVERLAY_CENTER_X + 3) - SUN_SIZE / 2 +
                                   (int)(Math.Cos(_clockRotation) * SUN_ORBIT_RADIUS),
                            Top = (OVERLAY_CENTER_X + 3) - SUN_SIZE / 2 +
                                  (int)(Math.Sin(_clockRotation) * SUN_ORBIT_RADIUS),
                            Children =
                            [
                                new ImageNode(OtherAssets.Sun, SUN_SIZE, SUN_SIZE)
                                {
                                    RotationRadians = _clockRotation - (float)Math.PI * 0.5f +
                                                      (float)Math.Sin(GradientOffset()) * 0.15f
                                }
                            ]
                        },

                        new BoxNode(SUN_SIZE, SUN_SIZE)
                        {
                            HorizontalAlignment = ItemsAlignment.Center,
                            VerticalAlignment = ItemsAlignment.Center,
                            IgnoreLayout = true,
                            Left = (OVERLAY_CENTER_X + 3) - SUN_SIZE / 2 +
                                   (int)(Math.Cos(_clockRotation + Math.PI) * SUN_ORBIT_RADIUS),
                            Top = (OVERLAY_CENTER_X + 3) - SUN_SIZE / 2 +
                                  (int)(Math.Sin(_clockRotation + Math.PI) * SUN_ORBIT_RADIUS),
                            Children =
                            [
                                new ImageNode(OtherAssets.Moon, SUN_SIZE, SUN_SIZE)
                                {
                                    RotationRadians = _clockRotation + (float)Math.PI * 0.5f +
                                                      (float)Math.Sin(GradientOffset()) * 0.15f
                                }
                            ]
                        }
                    ]
                },
                new BoxNode
                {
                    Style = new Style { Padding = new Insets(-2, 0, 0, 0) },
                    IgnoreLayout = true,
                    Children =
                    [
                        new TextNode(now.ToString("HH:mm"), 24, _theme.Text)
                        {
                            ShadowColor = Color.FromRgb(0, 0, 0, 0.9f),
                            ShadowDistance = 2,
                        },
                    ]
                },
            ],
        };
    }

    private BoxNode BuildNotificationsBadge(NotificationsSnapshot snapshot) => new()
    {
        Direction = Direction.Horizontal,
        VerticalAlignment = ItemsAlignment.Center,
        OnClick = _notificationService.ToggleDoNotDisturb,
        Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel, left: false) with { ShadowColor = null },
        Children =
        [
            ModulesCommon.BuildTextWithIcon(_theme, snapshot.DoNotDisturb ? Icons.BellOff : Icons.Bell, $"{snapshot.Count}")
        ],
    };

    private BoxNode BuildPopup(DateTime now, NotificationsSnapshot snapshot) => new()
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(_theme),
        Children =
        [
            new BoxNode(new Style { Spacing = 12 }, verticalAlignment: ItemsAlignment.Stretch)
            {
                _calendar.Draw(now), _worldClocks.Draw(now), _weather.Draw(OpenWeather)
            },
            ModulesCommon.BuildDivider(_theme.Border, height: 12),
            _notificationsWidget.Draw(snapshot),
        ],
    };

    private void OpenWeather()
    {
        _node.ClosePopup();
        _dialogs.Open<CompositeWindow>([_tabs.Get<WeatherTab>()]);
    }

    private static double GradientOffset() => (Environment.TickCount64 % 4600 / 4600.0) * Math.PI * 2;

    private float ClockTargetRotation(DateTime now)
    {
        var target = DayRotation(now);
        if (!_node.IsHovered || _clockBounds is not { } bounds || !Layout.Input.HasPointer)
        {
            return ClosestEquivalentAngle(target, _clockRotation);
        }

        var centerX = bounds.X + bounds.Width * 0.5f;
        var centerY = bounds.Y + bounds.Height * 0.5f;
        var pointerX = Layout.Input.PointerX - centerX;
        var pointerY = Layout.Input.PointerY - centerY;
        var distanceSquared = pointerX * pointerX + pointerY * pointerY;

        if (distanceSquared <= 0.01f)
        {
            return ClosestEquivalentAngle(target, _clockRotation);
        }

        var pointerAngle = MathF.Atan2(pointerY, pointerX);
        var isDaytime = now.Hour is >= 6 and < 18;
        target = isDaytime ? pointerAngle : pointerAngle - MathF.PI;

        return ClosestEquivalentAngle(target, _clockRotation);
    }

    private static float ClosestEquivalentAngle(float angle, float reference) =>
        reference + MathF.IEEERemainder(angle - reference, MathF.Tau);

    private static float DayRotation(DateTime now) =>
        (float)((now.TimeOfDay.TotalDays * Math.Tau) - 0.5 * Math.PI);

    private sealed class BoundsReportingBoxNode(int width, int height, Action<Rect> reportBounds)
        : BoxNode(width, height)
    {
        public override void Draw(IRenderApi renderer, int x, int y)
        {
            reportBounds(new Rect(x, y, Width, Height));
            base.Draw(renderer, x, y);
        }
    }

    private static EncodedImageData LoadClockImage(string path)
    {
        using var stream = typeof(CenterModule).Assembly.GetManifestResourceStream(path)
                           ?? throw new InvalidOperationException($"Embedded clock image '{path}' was not found.");
        using var buffer = new MemoryStream((int)stream.Length);
        stream.CopyTo(buffer);
        return new EncodedImageData("image/png", buffer.ToArray());
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

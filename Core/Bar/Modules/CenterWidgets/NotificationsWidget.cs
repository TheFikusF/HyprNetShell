using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class NotificationsWidget(NotificationService service, Theme theme)
{
    public const int WIDTH = CalendarWidget.WIDTH + 12 + WeatherWidget.WIDTH + 12 + WorldClocksWidget.WIDTH;
    private readonly RefFloat _doNotDisturbSwitchAnimation = new(service.Snapshot.DoNotDisturb ? 1.0f : 0.0f);

    public Node Draw(NotificationsSnapshot snapshot) => new BoxNode(WIDTH)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Center,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 8 },
        Children =
        [
            new BoxNode(new Style(), ItemsAlignment.Spread, ItemsAlignment.Center)
            {
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new ImageNode(snapshot.DoNotDisturb ? Icons.BellOff : Icons.Bell, 22, 22, theme.Text),
                    new TextNode("Notifications", 22, theme.Text),
                },

                new BoxNode(new Style { Spacing = 16 }, verticalAlignment: ItemsAlignment.Center)
                {
                    BuildDoNotDisturbToggle(snapshot.DoNotDisturb),
                    new BoxNode(2, 18) { Style = new Style { BackgroundColor = theme.Border } },
                    new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                    {
                        new TextNode($"{snapshot.Count}", 22, theme.Text),
                        BuildClearButton(snapshot.Count),
                    }
                }
            },
            ..BuildRows(snapshot),
        ],
    };

    private BoxNode BuildDoNotDisturbToggle(bool enabled) => new(height: 48)
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        OnClick = service.ToggleDoNotDisturb,
        Style = new Style { Spacing = 8 },
        Children =
        [
            new ImageNode(enabled ? Icons.BellOff : Icons.Bell, 18, 18, theme.Text),
            new SwitchNode(enabled, _doNotDisturbSwitchAnimation)
            {
                OffTrackColor = theme.Muted,
                OnTrackColor = theme.Active,
                KnobColor = theme.Text,
            },
        ],
    };

    private IEnumerable<Node> BuildRows(NotificationsSnapshot snapshot)
    {
        if (snapshot.Items.Count == 0)
        {
            yield return new BoxNode(height: 64)
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Children = [new TextNode("No notifications", 18, theme.Muted)]
            };
        }

        foreach (var notification in snapshot.Items.Take(5))
        {
            yield return NotificationCard.Draw(notification, service, theme);
        }
    }

    private BoxNode BuildClearButton(int count) => new()
    {
        VerticalAlignment = ItemsAlignment.Center,
        OnClick = count > 0 ? service.Clear : null,
        Opacity = count > 0 ? 1 : 0.45f,
        Style = new Style
        {
            BackgroundColor = theme.Muted,
            BorderRadius = 7,
            Padding = new Insets(8, 5),
            Spacing = 6,
        },
        Children =
        [
            new ImageNode(Icons.Trash, 18, 18, theme.Text),
            new TextNode("Clear", theme.TextSize, theme.Text),
        ],
    };
}
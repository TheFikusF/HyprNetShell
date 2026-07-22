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

    public Node Draw(NotificationsSnapshot snapshot) => new BoxNode(WIDTH)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 8 },
        Children =
        [
            new BoxNode(new Style(), ItemsAlignment.Spread, ItemsAlignment.Center)
            {
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new ImageNode(Icons.Bell, 22, 22, theme.Text),
                    new TextNode("Notifications", 22, theme.Text),
                },
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new TextNode($"{snapshot.Count}", 22, theme.Text),
                    BuildClearButton(snapshot.Count),
                }
            },
            ..BuildRows(snapshot),
        ],
    };

    private IEnumerable<Node> BuildRows(NotificationsSnapshot snapshot)
    {
        if (snapshot.Items.Count == 0)
        {
            yield return new TextNode("No notifications", 14, theme.Muted);
            yield break;
        }

        foreach (var notification in snapshot.Items.Take(5))
        {
            yield return NotificationCard.Draw(notification, service, theme);
        }
    }

    private Node BuildClearButton(int count) => new BoxNode
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
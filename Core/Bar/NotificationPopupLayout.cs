using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal static class NotificationPopupLayout
{
    private const int WIDTH = 460;
    private const int MAXIMUM_VISIBLE = 5;
    private static readonly Dictionary<uint, NotificationCard.State> CardStates = new();

    public static Node Draw(
        NotificationsSnapshot snapshot,
        NotificationService service,
        Theme theme,
        int screenHeight,
        int barHeight)
    {
        RemoveExpiredCardStates(snapshot.Items);
        var visible = snapshot.DoNotDisturb
            ? []
            : snapshot.Items
                .Where(notification => notification.PopupUntil > DateTime.UtcNow)
                .Take(MAXIMUM_VISIBLE)
                .ToArray();

        return new BoxNode(height: screenHeight)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.End,
            VerticalAlignment = ItemsAlignment.Start,
            Children =
            [
                new BoxNode(WIDTH + 6, screenHeight)
                {
                    Direction = Direction.Vertical,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Start,
                    Style = new Style
                    {
                        Padding = new Insets(barHeight + 4, 6, 0, 0),
                        Spacing = 8,
                    },
                    Children = [..visible.Select(notification => BuildToast(notification, service, theme))],
                }
            ],
        };
    }

    private static Node BuildToast(NotificationSnapshot notification, NotificationService service, Theme theme)
    {
        if (!CardStates.TryGetValue(notification.Id, out var state))
        {
            state = new NotificationCard.State();
            CardStates[notification.Id] = state;
        }

        return NotificationCard.Draw(notification, service, theme, state);
    }

    private static void RemoveExpiredCardStates(IReadOnlyList<NotificationSnapshot> notifications)
    {
        var activeIds = notifications.Select(notification => notification.Id).ToHashSet();
        foreach (var id in CardStates.Keys.Where(id => !activeIds.Contains(id)).ToArray())
        {
            CardStates.Remove(id);
        }
    }
}

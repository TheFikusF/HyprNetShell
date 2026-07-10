namespace HyprNetShell.Core.Models;

public sealed record NotificationsSnapshot(
    int Count,
    IReadOnlyList<NotificationSnapshot> Items)
{
    public static NotificationsSnapshot Empty { get; } = new(0, []);
}

public sealed record NotificationSnapshot(
    string Title,
    string Body,
    string AppName,
    string IconName = "");

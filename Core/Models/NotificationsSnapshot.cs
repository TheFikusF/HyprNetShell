using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Models;

public sealed record NotificationsSnapshot(
    int Count,
    IReadOnlyList<NotificationSnapshot> Items)
{
    public static NotificationsSnapshot Empty { get; } = new(0, []);
}

public sealed record NotificationSnapshot(
    uint Id,
    string Title,
    string Body,
    string AppName,
    string DesktopEntry,
    string IconName,
    RawImageData? ImageData,
    IReadOnlyList<NotificationActionSnapshot> Actions,
    bool Resident,
    DateTime ReceivedAt,
    DateTime PopupUntil);

public sealed record NotificationActionSnapshot(string Key, string Label);

namespace HyprNetShell.Core.Models;

public sealed record TrayItemSnapshot(
    string Id,
    string Title,
    string IconPath,
    PopupSnapshot? Menu,
    string BusName,
    string ObjectPath,
    string MenuPath);

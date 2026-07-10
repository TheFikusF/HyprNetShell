namespace HyprNetShell.Core.Models;

public sealed record BarSnapshot(
    IReadOnlyList<WorkspaceSnapshot> Workspaces,
    IReadOnlyList<MonitorWorkspaceSnapshot> MonitorWorkspaces,
    string FocusedTitle,
    string FocusedClassName,
    int FocusedWorkspaceId,
    SystemStatsSnapshot SystemStats,
    NetworkSnapshot Network,
    AudioSnapshot Audio,
    BluetoothSnapshot Bluetooth,
    NotificationsSnapshot Notifications,
    IReadOnlyList<BarModuleSnapshot> CenterModules,
    IReadOnlyList<BarModuleSnapshot> RightModules,
    IReadOnlyList<TrayItemSnapshot> TrayItems,
    PopupSnapshot? OpenPopup)
{
    public static BarSnapshot Empty { get; } = new(
        [],
        [],
        "Desktop",
        "",
        1,
        SystemStatsSnapshot.Empty,
        NetworkSnapshot.Empty,
        AudioSnapshot.Empty,
        BluetoothSnapshot.Empty,
        NotificationsSnapshot.Empty,
        [],
        [],
        [],
        null);
}

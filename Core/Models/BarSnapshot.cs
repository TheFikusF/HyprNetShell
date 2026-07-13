namespace HyprNetShell.Core.Models;

public sealed record BarSnapshot(
    SystemStatsSnapshot SystemStats,
    NetworkSnapshot Network,
    AudioSnapshot Audio,
    DisplayControlsSnapshot DisplayControls,
    BluetoothSnapshot Bluetooth,
    BatterySnapshot Battery,
    NotificationsSnapshot Notifications,
    IReadOnlyList<TrayItemSnapshot> TrayItems)
{
    public static BarSnapshot Empty { get; } = new(
        SystemStatsSnapshot.Empty,
        NetworkSnapshot.Empty,
        AudioSnapshot.Empty,
        DisplayControlsSnapshot.Empty,
        BluetoothSnapshot.Empty,
        BatterySnapshot.Empty,
        NotificationsSnapshot.Empty,
        []);
}

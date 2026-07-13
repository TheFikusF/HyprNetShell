using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Services;

public sealed class BarStateBuilder
{
    private readonly List<TrayItemSnapshot> _trayItems = [];

    public SystemStatsSnapshot SystemStats { get; set; } = SystemStatsSnapshot.Empty;
    public NetworkSnapshot Network { get; set; } = NetworkSnapshot.Empty;
    public AudioSnapshot Audio { get; set; } = AudioSnapshot.Empty;
    public BluetoothSnapshot Bluetooth { get; set; } = BluetoothSnapshot.Empty;
    public BatterySnapshot Battery { get; set; } = BatterySnapshot.Empty;
    public NotificationsSnapshot Notifications { get; set; } = NotificationsSnapshot.Empty;

    public void AddTrayItem(TrayItemSnapshot item) => _trayItems.Add(item);

    public BarSnapshot Build()
    {
        return new BarSnapshot(
            SystemStats,
            Network,
            Audio,
            Bluetooth,
            Battery,
            Notifications,
            _trayItems.ToArray());
    }
}

namespace HyprNetShell.Core.Models;

public sealed record BluetoothDeviceSnapshot(
    string Address,
    string Name,
    bool Connected,
    int? BatteryPercentage,
    string? Icon);

public sealed record BluetoothSnapshot(
    bool Available,
    IReadOnlyList<BluetoothDeviceSnapshot> Devices)
{
    public static BluetoothSnapshot Empty { get; } = new(false, []);
}

namespace HyprNetShell.Core.Models;

public enum DeviceBatterySource
{
    Laptop,
    Bluetooth,
    KdeConnect,
}

public sealed record DeviceBatterySnapshot(
    string Id,
    string Name,
    DeviceBatterySource Source,
    int Percentage,
    bool? IsCharging);

public sealed record DeviceBatteriesSnapshot(IReadOnlyList<DeviceBatterySnapshot> Devices)
{
    public static DeviceBatteriesSnapshot Empty { get; } = new([]);
}

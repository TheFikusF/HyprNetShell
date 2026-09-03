namespace HyprNetShell.Core.Models;

public sealed record BluetoothDeviceSnapshot(
    string Address,
    string Name,
    bool Connected,
    int? BatteryPercentage,
    string? Icon,
    bool Paired);

public sealed record BluetoothSnapshot(
    bool Available,
    bool Powered,
    IReadOnlyList<BluetoothDeviceSnapshot> Devices)
{
    public static BluetoothSnapshot Empty { get; } = new(false, false, []);
}

internal readonly record struct BluetoothOperationResult(bool Success, string? Error)
{
    internal static BluetoothOperationResult Succeeded { get; } = new(true, null);
    internal static BluetoothOperationResult Failed(string error) => new(false, error);
}

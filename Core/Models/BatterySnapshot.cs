namespace HyprNetShell.Core.Models;

public sealed record BatterySnapshot(
    bool Available,
    string Device,
    int Percentage,
    string Status)
{
    public bool IsCharging => Status.Equals("Charging", StringComparison.OrdinalIgnoreCase);
    public bool IsCritical => Percentage <= 15 && !IsCharging;

    public static BatterySnapshot Empty { get; } = new(false, "", 0, "Unknown");
}

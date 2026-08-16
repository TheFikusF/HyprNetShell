namespace HyprNetShell.Core.Models;

public sealed record BatterySnapshot(
    bool Available,
    string Device,
    int Percentage,
    string Status,
    int? ChargeLimit,
    PowerProfileSnapshot PowerProfiles)
{
    public bool IsCharging => Status.Equals("Charging", StringComparison.OrdinalIgnoreCase);
    public bool IsCritical => Percentage <= 15 && !IsCharging;

    public static BatterySnapshot Empty { get; } = new(false, "", 0, "Unknown", null, PowerProfileSnapshot.Empty);
}

public sealed record PowerProfileSnapshot(
    bool Available,
    string Active,
    IReadOnlyList<string> Profiles)
{
    public static PowerProfileSnapshot Empty { get; } = new(false, "", []);
}

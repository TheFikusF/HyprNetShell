namespace HyprNetShell.Core.Models;

public sealed record SystemStatsSnapshot(
    int? CpuPercent,
    int? RamPercent,
    int? TemperatureCelsius)
{
    public static SystemStatsSnapshot Empty { get; } = new(null, null, null);
}

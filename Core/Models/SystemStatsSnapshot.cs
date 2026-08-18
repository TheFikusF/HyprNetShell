namespace HyprNetShell.Core.Models;

public sealed record SystemStatsSnapshot(
    int? CpuPercent,
    int? GpuPercent,
    int? RamPercent,
    int? SwapPercent,
    int? TemperatureCelsius,
    long DownloadBytesPerSecond,
    long UploadBytesPerSecond,
    IReadOnlyList<float> CpuHistory,
    IReadOnlyList<float> GpuHistory,
    IReadOnlyList<float> RamHistory,
    IReadOnlyList<float> SwapHistory,
    IReadOnlyList<float> DownloadHistory,
    IReadOnlyList<float> UploadHistory,
    IReadOnlyList<DiskUsageSnapshot> Disks)
{
    public static SystemStatsSnapshot Empty { get; } = new(
        null, null, null, null, null, 0, 0, [], [], [], [], [], [], []);
}

public sealed record DiskUsageSnapshot(
    string Name,
    long TotalBytes,
    long UsedBytes)
{
    public int Percent => TotalBytes > 0
        ? Math.Clamp((int)Math.Round(UsedBytes * 100.0 / TotalBytes), 0, 100)
        : 0;
}

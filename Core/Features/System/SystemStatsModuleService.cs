using System.Diagnostics;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class SystemStatsModuleService : IBarDataService
{
    private const int HISTORY_CAPACITY = 56;

    private CpuSample? _previousCpuSample;
    private NetworkSample? _previousNetworkSample;
    private string? _gpuUtilizationPath;
    private bool _gpuUtilizationPathSearched;
    private readonly Queue<float> _cpuHistory = new(HISTORY_CAPACITY);
    private readonly Queue<float> _gpuHistory = new(HISTORY_CAPACITY);
    private readonly Queue<float> _ramHistory = new(HISTORY_CAPACITY);
    private readonly Queue<float> _swapHistory = new(HISTORY_CAPACITY);
    private readonly Queue<float> _downloadHistory = new(HISTORY_CAPACITY);
    private readonly Queue<float> _uploadHistory = new(HISTORY_CAPACITY);

    public SystemStatsSnapshot Snapshot { get; private set; } = SystemStatsSnapshot.Empty;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var cpuPercent = ReadCpuPercent();
        var gpuPercent = await ReadGpuPercentAsync(cancellationToken);
        var (ramPercent, swapPercent) = ReadMemoryPercentages();
        var temperatureCelsius = ReadTemperatureCelsius();
        var (downloadBytesPerSecond, uploadBytesPerSecond) = ReadNetworkRates();

        Append(_cpuHistory, cpuPercent.GetValueOrDefault());
        Append(_gpuHistory, gpuPercent.GetValueOrDefault());
        Append(_ramHistory, ramPercent.GetValueOrDefault());
        Append(_swapHistory, swapPercent.GetValueOrDefault());
        Append(_downloadHistory, downloadBytesPerSecond);
        Append(_uploadHistory, uploadBytesPerSecond);

        Snapshot = new SystemStatsSnapshot(
            cpuPercent,
            gpuPercent,
            ramPercent,
            swapPercent,
            temperatureCelsius,
            downloadBytesPerSecond,
            uploadBytesPerSecond,
            [.._cpuHistory],
            [.._gpuHistory],
            [.._ramHistory],
            [.._swapHistory],
            [.._downloadHistory],
            [.._uploadHistory],
            ReadDisks());
    }

    private static void Append(Queue<float> history, float value)
    {
        if (history.Count == HISTORY_CAPACITY)
        {
            history.Dequeue();
        }

        history.Enqueue(Math.Max(0.0f, value));
    }

    private int? ReadCpuPercent()
    {
        var sample = ReadCpuSample();
        if (sample is null)
        {
            return null;
        }

        var previous = _previousCpuSample;
        _previousCpuSample = sample;
        if (previous is null)
        {
            return 0;
        }

        var totalDelta = sample.Value.Total - previous.Value.Total;
        var idleDelta = sample.Value.Idle - previous.Value.Idle;
        if (totalDelta <= 0)
        {
            return null;
        }

        return ClampPercent((int)MathF.Round((1.0f - (float)idleDelta / totalDelta) * 100.0f));
    }

    private static CpuSample? ReadCpuSample()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault();
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("cpu ", StringComparison.Ordinal))
            {
                return null;
            }

            var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Skip(1)
                .Select(value => ulong.TryParse(value, out var parsed) ? parsed : 0UL)
                .ToArray();
            if (values.Length < 5)
            {
                return null;
            }

            var idle = values[3] + values[4];
            var total = values.Aggregate(0UL, (sum, value) => sum + value);
            return new CpuSample(total, idle);
        }
        catch
        {
            return null;
        }
    }

    private static (int? Ram, int? Swap) ReadMemoryPercentages()
    {
        try
        {
            ulong? total = null;
            ulong? available = null;
            ulong? swapTotal = null;
            ulong? swapFree = null;

            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
                {
                    total = ParseMeminfoValue(line);
                }
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
                {
                    available = ParseMeminfoValue(line);
                }
                else if (line.StartsWith("SwapTotal:", StringComparison.Ordinal))
                {
                    swapTotal = ParseMeminfoValue(line);
                }
                else if (line.StartsWith("SwapFree:", StringComparison.Ordinal))
                {
                    swapFree = ParseMeminfoValue(line);
                }
            }

            int? ramPercent = total.HasValue && available.HasValue && total.Value > 0
                ? ClampPercent((int)MathF.Round((1.0f - (float)available.Value / total.Value) * 100.0f))
                : null;
            int? swapPercent = swapTotal.HasValue && swapFree.HasValue
                ? swapTotal.Value == 0
                    ? 0
                    : ClampPercent((int)MathF.Round((1.0f - (float)swapFree.Value / swapTotal.Value) * 100.0f))
                : null;

            return (ramPercent, swapPercent);
        }
        catch
        {
            return (null, null);
        }
    }

    private static ulong? ParseMeminfoValue(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && ulong.TryParse(parts[1], out var value) ? value : null;
    }

    private async Task<int?> ReadGpuPercentAsync(CancellationToken cancellationToken)
    {
        if (!_gpuUtilizationPathSearched)
        {
            _gpuUtilizationPathSearched = true;
            try
            {
                foreach (var card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
                {
                    string[] candidates =
                    {
                        Path.Combine(card, "device", "gpu_busy_percent"),
                        Path.Combine(card, "device", "gt_busy_percent"),
                        Path.Combine(card, "gt", "gt0", "gt_busy_percent"),
                    };
                    _gpuUtilizationPath = candidates.FirstOrDefault(File.Exists);
                    if (_gpuUtilizationPath is not null)
                    {
                        break;
                    }
                }
            }
            catch
            {
                // Fall back to the vendor utility when sysfs cannot be inspected.
            }
        }

        if (_gpuUtilizationPath is not null)
        {
            try
            {
                if (int.TryParse((await File.ReadAllTextAsync(_gpuUtilizationPath, cancellationToken)).Trim(), out var percent))
                {
                    return ClampPercent(percent);
                }
            }
            catch
            {
                _gpuUtilizationPath = null;
            }
        }

        var output = await CommandRunner.TryReadAsync(
            "nvidia-smi",
            "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        var firstLine = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return int.TryParse(firstLine, out var gpuPercent) ? ClampPercent(gpuPercent) : null;
    }

    private (long Download, long Upload) ReadNetworkRates()
    {
        var counters = ReadNetworkCounters();
        if (counters is null)
        {
            return (0, 0);
        }

        var now = Stopwatch.GetTimestamp();
        var previous = _previousNetworkSample;
        _previousNetworkSample = new NetworkSample(counters.Value.Received, counters.Value.Sent, now);
        if (previous is null)
        {
            return (0, 0);
        }

        var elapsed = Stopwatch.GetElapsedTime(previous.Value.Timestamp, now).TotalSeconds;
        if (elapsed <= 0.0 || counters.Value.Received < previous.Value.Received || counters.Value.Sent < previous.Value.Sent)
        {
            return (0, 0);
        }

        return (
            Math.Max(0, (long)((counters.Value.Received - previous.Value.Received) / elapsed)),
            Math.Max(0, (long)((counters.Value.Sent - previous.Value.Sent) / elapsed)));
    }

    private static (ulong Received, ulong Sent)? ReadNetworkCounters()
    {
        try
        {
            var defaultInterface = ReadDefaultNetworkInterface();
            ulong received = 0;
            ulong sent = 0;
            var found = false;
            foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
            {
                var separator = line.IndexOf(':');
                if (separator < 0)
                {
                    continue;
                }

                var interfaceName = line[..separator].Trim();
                if (defaultInterface is not null
                    ? !interfaceName.Equals(defaultInterface, StringComparison.Ordinal)
                    : interfaceName.Equals("lo", StringComparison.Ordinal))
                {
                    continue;
                }

                var fields = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 9 || !ulong.TryParse(fields[0], out var interfaceReceived) ||
                    !ulong.TryParse(fields[8], out var interfaceSent))
                {
                    continue;
                }

                received += interfaceReceived;
                sent += interfaceSent;
                found = true;
            }

            return found ? (received, sent) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadDefaultNetworkInterface()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/net/route").Skip(1))
            {
                var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length > 3 && fields[1] == "00000000" &&
                    int.TryParse(fields[3], global::System.Globalization.NumberStyles.HexNumber, null, out var flags) &&
                    (flags & 0x1) != 0)
                {
                    return fields[0];
                }
            }
        }
        catch
        {
            // Aggregate non-loopback interfaces when no default route can be read.
        }

        return null;
    }

    private static IReadOnlyList<DiskUsageSnapshot> ReadDisks()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable)
                .Where(drive => drive.TotalSize > 0)
                .GroupBy(drive => drive.RootDirectory.FullName, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(drive => drive.RootDirectory.FullName.Length)
                .ThenBy(drive => drive.RootDirectory.FullName, StringComparer.Ordinal)
                .Take(8)
                .Select(drive => new DiskUsageSnapshot(
                    drive.RootDirectory.FullName,
                    drive.TotalSize,
                    Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace)))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static int? ReadTemperatureCelsius()
    {
        var thermalTemperature = ReadTemperatureFromDirectories("/sys/class/thermal", "thermal_zone*", "temp");
        if (thermalTemperature.HasValue)
        {
            return thermalTemperature.Value;
        }

        return ReadTemperatureFromDirectories("/sys/class/hwmon", "hwmon*", "temp*_input");
    }

    private static int? ReadTemperatureFromDirectories(string root, string directoryPattern, string filePattern)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return null;
            }

            foreach (var directory in Directory.EnumerateDirectories(root, directoryPattern))
            {
                foreach (var path in Directory.EnumerateFiles(directory, filePattern))
                {
                    if (!int.TryParse(File.ReadAllText(path).Trim(), out var raw))
                    {
                        continue;
                    }

                    var celsius = raw > 1000 ? raw / 1000 : raw;
                    if (celsius is > 0 and < 130)
                    {
                        return celsius;
                    }
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static int ClampPercent(int value) => Math.Clamp(value, 0, 100);

    private readonly record struct CpuSample(ulong Total, ulong Idle);
    private readonly record struct NetworkSample(ulong Received, ulong Sent, long Timestamp);
}

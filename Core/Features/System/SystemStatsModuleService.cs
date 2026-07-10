using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class SystemStatsModuleService : IBarDataService
{
    private CpuSample? _previousCpuSample;

    public ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        state.SystemStats = ReadSnapshot();
        return ValueTask.CompletedTask;
    }

    private SystemStatsSnapshot ReadSnapshot()
    {
        var cpuPercent = ReadCpuPercent();
        var ramPercent = ReadRamPercent();
        var temperatureCelsius = ReadTemperatureCelsius();

        return new SystemStatsSnapshot(cpuPercent, ramPercent, temperatureCelsius);
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

    private static int? ReadRamPercent()
    {
        try
        {
            ulong? total = null;
            ulong? available = null;

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

                if (total.HasValue && available.HasValue)
                {
                    break;
                }
            }

            if (!total.HasValue || !available.HasValue || total.Value == 0)
            {
                return null;
            }

            return ClampPercent((int)MathF.Round((1.0f - (float)available.Value / total.Value) * 100.0f));
        }
        catch
        {
            return null;
        }
    }

    private static ulong? ParseMeminfoValue(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && ulong.TryParse(parts[1], out var value) ? value : null;
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
}

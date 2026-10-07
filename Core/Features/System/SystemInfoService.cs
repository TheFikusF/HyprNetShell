using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

internal sealed class SystemInfoService : IDisposable
{
    private const long BYTES_PER_KIB = 1024;
    private static readonly TimeSpan CACHE_INTERVAL = TimeSpan.FromSeconds(30);

    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private bool _isCollecting;
    private SystemInfoSnapshot _snapshot = SystemInfoSnapshot.Empty;
    private long? _lastRefreshTimestamp;
    private bool _disposed;

    public SystemInfoSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public void Refresh()
    {
        lock (_stateLock)
        {
            if (_disposed || _isCollecting)
            {
                return;
            }

            if (_lastRefreshTimestamp.HasValue &&
                Stopwatch.GetElapsedTime(_lastRefreshTimestamp.Value) < CACHE_INTERVAL)
            {
                return;
            }

            Volatile.Write(ref _snapshot, new SystemInfoSnapshot(_snapshot.Entries, true));
            _isCollecting = true;
            _ = Task.Run(() => CollectAndPublish(_lifetime.Token));
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            Volatile.Write(ref _snapshot, new SystemInfoSnapshot(_snapshot.Entries, false));
            if (!_isCollecting)
            {
                _lifetime.Dispose();
            }
        }
    }

    private void CollectAndPublish(CancellationToken cancellationToken)
    {
        try
        {
            var entries = Collect(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateLock)
            {
                if (!_disposed)
                {
                    Volatile.Write(ref _snapshot, new SystemInfoSnapshot(entries, false));
                    _lastRefreshTimestamp = Stopwatch.GetTimestamp();
                }
            }
        }

        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        catch (Exception exception)
        {
            AppLogger.Warning("SystemInfo", "Could not collect system information; keeping the previous snapshot", exception);
        }

        finally
        {
            lock (_stateLock)
            {
                if (_snapshot.IsRefreshing)
                {
                    Volatile.Write(ref _snapshot, new SystemInfoSnapshot(_snapshot.Entries, false));
                }

                _isCollecting = false;

                // Shutdown never waits for filesystem reads; the worker releases its token source.
                if (_disposed)
                {
                    _lifetime.Dispose();
                }
            }
        }
    }

    private static List<SystemInfoEntry> Collect(CancellationToken cancellationToken)
    {
        var entries = new List<SystemInfoEntry>();
        var osPath = "/etc/os-release";
        var osText = ReadOptional(osPath, cancellationToken);
        if (osText is null)
        {
            osPath = "/usr/lib/os-release";
            osText = ReadOptional(osPath, cancellationToken);
        }

        if (osText is not null)
        {
            var os = ParseFields(osText, '=', osPath);
            var name = os.GetValueOrDefault("PRETTY_NAME");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = string.Join(" ", new[] { os.GetValueOrDefault("NAME"), os.GetValueOrDefault("VERSION_ID") }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            }

            Add(entries, "OS", name);
        }

        Add(entries, "Kernel", ReadOptional("/proc/sys/kernel/osrelease", cancellationToken));
        Add(entries, "Architecture", RuntimeInformation.OSArchitecture.ToString());
        Add(entries, "Hostname", Environment.MachineName);
        CollectCpu(entries, cancellationToken);
        Add(entries, "Hardware vendor", ReadOptional("/sys/class/dmi/id/sys_vendor", cancellationToken));
        Add(entries, "Hardware model", ReadOptional("/sys/class/dmi/id/product_name", cancellationToken));
        CollectUptime(entries, cancellationToken);
        CollectMemory(entries, cancellationToken);
        Add(entries, "Session", Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"));
        Add(entries, "Desktop", Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
            ?? Environment.GetEnvironmentVariable("XDG_SESSION_DESKTOP"));
        Add(entries, "Login shell", Environment.GetEnvironmentVariable("SHELL"));
        cancellationToken.ThrowIfCancellationRequested();
        return entries;
    }

    private static void CollectCpu(List<SystemInfoEntry> entries, CancellationToken cancellationToken)
    {
        const string CPU_PATH = "/proc/cpuinfo";
        var text = ReadOptional(CPU_PATH, cancellationToken);
        if (text is null)
        {
            return;
        }

        var models = new HashSet<string>(StringComparer.Ordinal);
        var threads = 0;
        foreach (var line in text.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var separator = line.IndexOf(':');
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            if (key == "processor" && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                threads++;
            }

            if (key == "model name" && value.Length > 0)
            {
                models.Add(value);
            }
        }

        if (models.Count == 0)
        {
            var fields = ParseFields(text, ':', CPU_PATH);
            Add(entries, "CPU", fields.GetValueOrDefault("Hardware") ?? fields.GetValueOrDefault("Model"));
        }

        else
        {
            Add(entries, "CPU", string.Join(" / ", models));
        }

        if (threads > 0)
        {
            Add(entries, "CPU threads", threads.ToString(CultureInfo.InvariantCulture));
        }

        else
        {
            AppLogger.Warning("SystemInfo", $"Could not parse logical CPU count from {CPU_PATH}; using the process-visible count");
            Add(entries, "CPU threads (process-visible)", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void CollectUptime(List<SystemInfoEntry> entries, CancellationToken cancellationToken)
    {
        const string UPTIME_PATH = "/proc/uptime";
        var text = ReadOptional(UPTIME_PATH, cancellationToken);
        if (text is null)
        {
            return;
        }

        var value = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            AppLogger.Warning("SystemInfo", $"Could not parse uptime seconds from {UPTIME_PATH}; omitting uptime");
            return;
        }

        var uptime = TimeSpan.FromSeconds(seconds);
        Add(entries, "Uptime", $"{uptime.Days}d {uptime.Hours}h {uptime.Minutes}m");
    }

    private static void CollectMemory(List<SystemInfoEntry> entries, CancellationToken cancellationToken)
    {
        const string MEMORY_PATH = "/proc/meminfo";
        var text = ReadOptional(MEMORY_PATH, cancellationToken);
        if (text is null)
        {
            return;
        }

        var fields = ParseFields(text, ':', MEMORY_PATH);
        AddMemory(entries, "RAM", fields, "MemTotal", "MemAvailable", MEMORY_PATH);
        AddMemory(entries, "Swap", fields, "SwapTotal", "SwapFree", MEMORY_PATH);
    }

    private static void AddMemory(
        List<SystemInfoEntry> entries,
        string label,
        Dictionary<string, string> fields,
        string totalKey,
        string freeKey,
        string path)
    {
        if (!TryReadKib(fields.GetValueOrDefault(totalKey), out var total) ||
            !TryReadKib(fields.GetValueOrDefault(freeKey), out var free) || free > total)
        {
            AppLogger.Warning("SystemInfo", $"Could not parse {totalKey}/{freeKey} in {path}; omitting {label}");
            return;
        }

        Add(entries, label, total == 0 ? "Disabled" : $"{FormatBytes(total - free)} / {FormatBytes(total)}");
    }

    private static bool TryReadKib(string? text, out long bytes)
    {
        bytes = 0;
        var parts = text?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts is not { Length: 2 } || parts[1] != "kB" ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var kib) ||
            kib > long.MaxValue / BYTES_PER_KIB)
        {
            return false;
        }

        bytes = kib * BYTES_PER_KIB;
        return true;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private static Dictionary<string, string> ParseFields(string text, char separator, string path)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var index = line.IndexOf(separator);
            if (index <= 0)
            {
                AppLogger.Warning("SystemInfo", $"Could not parse a key/value line in {path}; skipping the malformed line");
                continue;
            }

            var value = line[(index + 1)..].Trim();
            if (separator == '=' && value.Length > 0 && value[0] is '\"' or '\'')
            {
                var quote = value[0];
                if (value.Length < 2 || value[^1] != quote)
                {
                    AppLogger.Warning("SystemInfo", $"Could not parse a quoted value in {path}; skipping the malformed field");
                    continue;
                }

                value = value[1..^1];
                if (quote == '\"')
                {
                    value = value.Replace("\\\"", "\"").Replace("\\$", "$").Replace("\\`", "`").Replace("\\\\", "\\");
                }
            }

            fields[line[..index].Trim()] = value;
        }

        return fields;
    }

    private static string? ReadOptional(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var text = File.ReadAllText(path).Trim();
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length == 0)
            {
                AppLogger.Warning("SystemInfo", $"System information source {path} is empty; omitting its value");
                return null;
            }

            return text;
        }

        catch (FileNotFoundException)
        {
            return null;
        }

        catch (DirectoryNotFoundException)
        {
            return null;
        }

        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("SystemInfo", $"Could not read system information from {path}; omitting its value", exception);
            return null;
        }
    }

    private static void Add(List<SystemInfoEntry> entries, string label, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            entries.Add(new SystemInfoEntry(label, value.Trim()));
        }
    }
}

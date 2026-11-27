using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Native.Interop.Authorization;

namespace HyprNetShell.Core.Features.System;

internal sealed class DriveHealthService(DesktopAuthorization? authorization) : IDisposable
{
    private const int STANDBY_EXIT_CODE = 99;
    private const int OPEN_ERROR_MASK = 0b11;
    private const int SMART_COMMAND_ERROR_MASK = 0b100;
    private const int KERNEL_SECTOR_BYTES = 512;
    private const int NVME_DATA_UNIT_BYTES = 512_000;

    private static readonly TimeSpan PROBE_TIMEOUT = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _lifetime = new();
    private Task? _check;

    public IReadOnlyList<DriveHealthSnapshot>? Snapshot { get; private set; }
    public bool Checking => _check is { IsCompleted: false };

    public void Check()
    {
        if (!Checking)
        {
            var token = _lifetime.Token;
            _check = Task.Run(() => CheckAsync(token), token);
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = Directory.EnumerateDirectories("/sys/block")
                .Where(path => Directory.Exists(Path.Combine(path, "device")))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var results = await Task.WhenAll(devices.Select(async path =>
            {
                var device = Path.Combine("/dev", Path.GetFileName(path));
                var (read, written) = ReadSessionIo(path);
                var (diagnostics, accessDenied) = await ReadDiagnosticsAsync(device, cancellationToken);
                return (Drive: new DriveHealthSnapshot(device, diagnostics, read, written), AccessDenied: accessDenied);
            }));
            var drives = results.Select(result => result.Drive).ToArray();
            Snapshot = drives;
            var denied = results.Where(result => result.AccessDenied).Select(result => result.Drive.DevicePath).ToArray();
            if (denied.Length != 0 && authorization is not null)
            {
                var output = await authorization.ReadSmartAsync(denied, cancellationToken);
                if (output is not null)
                {
                    var elevated = ParseElevatedResults(denied, output);
                    Snapshot = drives.Select(drive => elevated.TryGetValue(drive.DevicePath, out var diagnostics)
                        ? drive with { Diagnostics = diagnostics } : drive).ToArray();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("DriveHealth", "Could not enumerate block devices in /sys/block.", exception);
            Snapshot ??= [];
        }
    }

    private static (decimal? Read, decimal? Written) ReadSessionIo(string path)
    {
        try
        {
            var fields = File.ReadAllText(Path.Combine(path, "stat"))
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length > 6 &&
                ulong.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var read) &&
                ulong.TryParse(fields[6], NumberStyles.None, CultureInfo.InvariantCulture, out var written))
            {
                // Linux counts these in 512-byte sectors regardless of the device's logical block size.
                return ((decimal)read * KERNEL_SECTOR_BYTES, (decimal)written * KERNEL_SECTOR_BYTES);
            }

            AppLogger.Warning("DriveHealth", $"Unexpected block I/O counters in {path}/stat.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLogger.Warning("DriveHealth", $"Could not read block I/O counters in {path}/stat.", exception);
        }

        return (null, null);
    }

    private static async Task<(DriveDiagnostics Diagnostics, bool AccessDenied)> ReadDiagnosticsAsync(string device, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo("smartctl") {
                UseShellExecute = false,
                Environment = { ["LC_ALL"] = "C" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-j", "-i", "-H", "-A", "-l", "devstat", "-n", $"standby,{STANDBY_EXIT_CODE}", "--", device },
            };
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start smartctl.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PROBE_TIMEOUT);
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var error = process.StandardError.ReadToEndAsync(timeout.Token);
                var streams = Task.WhenAll(output, error);
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), streams);
                if (process.ExitCode == STANDBY_EXIT_CODE)
                {
                    return (new(DriveHealth.Standby), false);
                }

                var results = await streams;
                using var document = JsonDocument.Parse(results[0]);
                var diagnostics = ParseDiagnostics(document.RootElement, process.ExitCode);
                var detail = ReadSmartMessages(document.RootElement) ?? results[1].Trim();
                if (diagnostics.Health == DriveHealth.Unavailable || (process.ExitCode & SMART_COMMAND_ERROR_MASK) != 0)
                {
                    AppLogger.Warning("DriveHealth", $"SMART diagnostics may be incomplete for {device} (smartctl exit {process.ExitCode}). {detail}");
                }

                var denied = (process.ExitCode & OPEN_ERROR_MASK) != 0 &&
                    (detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ||
                     detail.Contains("Operation not permitted", StringComparison.OrdinalIgnoreCase));
                return (diagnostics, denied);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }

                cancellationToken.ThrowIfCancellationRequested();
                AppLogger.Warning("DriveHealth", $"SMART health check timed out for {device}.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or JsonException)
        {
            AppLogger.Warning("DriveHealth", $"Could not read SMART diagnostics for {device}; check that smartctl is installed and can access the device.", exception);
        }

        return (new(DriveHealth.Unavailable), false);
    }

    private static Dictionary<string, DriveDiagnostics> ParseElevatedResults(string[] devices, string output)
    {
        var results = new Dictionary<string, DriveDiagnostics>(StringComparer.Ordinal);
        var parts = output.Split('\u001e');
        if (parts.Length != devices.Length * 2 + 1 || parts[^1].Length != 0)
        {
            AppLogger.Warning("DriveHealth", "Unexpected output from the elevated SMART check.");
            return results;
        }

        for (var index = 0; index < devices.Length; index++)
        {
            var device = devices[index];
            if (!int.TryParse(parts[index * 2 + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var exitCode))
            {
                AppLogger.Warning("DriveHealth", $"Missing smartctl exit status for {device}.");
                continue;
            }

            if (exitCode == STANDBY_EXIT_CODE)
            {
                results[device] = new(DriveHealth.Standby);
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(parts[index * 2]);
                var diagnostics = ParseDiagnostics(document.RootElement, exitCode);
                if (diagnostics.Health == DriveHealth.Unavailable || (exitCode & SMART_COMMAND_ERROR_MASK) != 0)
                {
                    AppLogger.Warning("DriveHealth", $"Elevated SMART diagnostics may be incomplete for {device} (smartctl exit {exitCode}). {ReadSmartMessages(document.RootElement)}");
                }

                results[device] = diagnostics;
            }
            catch (JsonException exception)
            {
                AppLogger.Warning("DriveHealth", $"Could not parse elevated SMART diagnostics for {device} (smartctl exit {exitCode}).", exception);
            }
        }

        return results;
    }

    private static string? ReadSmartMessages(JsonElement root)
    {
        var messages = GetProperty(GetProperty(root, "smartctl"), "messages");
        return messages.ValueKind == JsonValueKind.Array
            ? string.Join("; ", messages.EnumerateArray().Select(message => GetString(message, "string"))) : null;
    }

    private static DriveDiagnostics ParseDiagnostics(JsonElement root, int exitCode)
    {
        // A nonzero exit code can still contain a valid health result and useful telemetry.
        var passed = GetProperty(GetProperty(root, "smart_status"), "passed").ValueKind;
        var health = passed switch {
            JsonValueKind.False => DriveHealth.Failing,
            JsonValueKind.True when (exitCode & OPEN_ERROR_MASK) == 0 => DriveHealth.Passed,
            _ => DriveHealth.Unavailable,
        };
        var nvme = GetProperty(root, "nvme_smart_health_information_log");
        if (nvme.ValueKind == JsonValueKind.Object)
        {
            return new(
                health,
                (decimal?)GetUnsigned(nvme, "data_units_read") * NVME_DATA_UNIT_BYTES,
                (decimal?)GetUnsigned(nvme, "data_units_written") * NVME_DATA_UNIT_BYTES,
                GetUnsigned(nvme, "percentage_used"),
                GetUnsigned(nvme, "media_errors"));
        }

        // ATA vendor attributes disagree on units and meaning. Only use standardized Device Statistics.
        var pages = GetProperty(GetProperty(root, "ata_device_statistics"), "pages");
        if (pages.ValueKind != JsonValueKind.Array)
        {
            return new(health);
        }

        ulong? sectorsRead = null;
        ulong? sectorsWritten = null;
        ulong? wear = null;
        foreach (var page in pages.EnumerateArray())
        {
            var table = GetProperty(page, "table");
            if (table.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var entry in table.EnumerateArray())
            {
                if (GetProperty(GetProperty(entry, "flags"), "valid").ValueKind != JsonValueKind.True)
                {
                    continue;
                }

                switch (GetString(entry, "name"))
                {
                    case "Logical Sectors Read":
                        sectorsRead = GetUnsigned(entry, "value");
                        break;
                    case "Logical Sectors Written":
                        sectorsWritten = GetUnsigned(entry, "value");
                        break;
                    case "Percentage Used Endurance Indicator":
                        wear = GetUnsigned(entry, "value");
                        break;
                }
            }
        }

        var sectorBytes = GetUnsigned(root, "logical_block_size") is > 0 and <= uint.MaxValue and { } size ? (uint?)size : null;
        return new(health, (decimal?)sectorsRead * sectorBytes, (decimal?)sectorsWritten * sectorBytes, wear);
    }

    private static JsonElement GetProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? GetString(JsonElement element, string name) =>
        GetProperty(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static ulong? GetUnsigned(JsonElement element, string name) =>
        GetProperty(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetUInt64(out var number) ? number : null;
}

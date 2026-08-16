using System.Globalization;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class BatteryModuleService(string device = "BAT0") : IBarDataService
{
    internal const int MINIMUM_CHARGE_LIMIT = 60;
    internal const int MAXIMUM_CHARGE_LIMIT = 100;
    internal const int CHARGE_LIMIT_STEP = 5;

    private static readonly TimeSpan PowerProfilesRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly string _chargeLimitConfigPath = GetChargeLimitConfigPath();
    private PowerProfileSnapshot _powerProfiles = PowerProfileSnapshot.Empty;
    private DateTime _powerProfilesObservedAtUtc = DateTime.MinValue;
    private int? _savedChargeLimit;
    private bool _chargeLimitConfigLoaded;
    private bool _startupChargeLimitApplied;

    public BatterySnapshot Snapshot { get; private set; } = BatterySnapshot.Empty;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var basePath = Path.Combine("/sys/class/power_supply", device);
        var capacityText = Read(Path.Combine(basePath, "capacity"));
        if (!int.TryParse(capacityText, out var capacity))
        {
            Snapshot = BatterySnapshot.Empty;
            return;
        }

        var status = Read(Path.Combine(basePath, "status"));
        var chargeLimitText = Read(Path.Combine(basePath, "charge_control_end_threshold"));
        var chargeLimit = int.TryParse(chargeLimitText, out var parsedChargeLimit)
            ? Math.Clamp(parsedChargeLimit, MINIMUM_CHARGE_LIMIT, MAXIMUM_CHARGE_LIMIT)
            : (int?)null;
        int? startupChargeLimit = null;
        if (chargeLimit.HasValue && !_startupChargeLimitApplied)
        {
            EnsureChargeLimitConfigLoaded();
            _startupChargeLimitApplied = true;
            if (_savedChargeLimit is { } savedChargeLimit)
            {
                chargeLimit = savedChargeLimit;
                startupChargeLimit = savedChargeLimit;
            }
        }

        if (DateTime.UtcNow - _powerProfilesObservedAtUtc >= PowerProfilesRefreshInterval)
        {
            var powerProfilesOutput = await CommandRunner.TryReadAsync(
                "powerprofilesctl",
                "list",
                TimeSpan.FromMilliseconds(900),
                cancellationToken);
            _powerProfiles = ParsePowerProfiles(powerProfilesOutput);
            _powerProfilesObservedAtUtc = DateTime.UtcNow;
        }

        Snapshot = new BatterySnapshot(
            true,
            device,
            Math.Clamp(capacity, 0, 100),
            string.IsNullOrWhiteSpace(status) ? "Unknown" : status,
            chargeLimit,
            _powerProfiles);

        if (startupChargeLimit is { } limitToApply)
        {
            _ = ApplyChargeLimitAsync(limitToApply);
        }
    }

    internal void SetChargeLimit(int chargeLimit)
    {
        if (Snapshot.ChargeLimit is null)
        {
            return;
        }

        chargeLimit = Math.Clamp(chargeLimit, MINIMUM_CHARGE_LIMIT, MAXIMUM_CHARGE_LIMIT);
        _savedChargeLimit = chargeLimit;
        _chargeLimitConfigLoaded = true;
        PersistChargeLimit(chargeLimit);
        Snapshot = Snapshot with { ChargeLimit = chargeLimit };
        _ = ApplyChargeLimitAsync(chargeLimit);
    }

    private void EnsureChargeLimitConfigLoaded()
    {
        if (_chargeLimitConfigLoaded)
        {
            return;
        }

        _savedChargeLimit = ReadChargeLimitConfig();
        _chargeLimitConfigLoaded = true;
    }

    private int? ReadChargeLimitConfig()
    {
        try
        {
            var text = File.ReadAllText(_chargeLimitConfigPath).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var chargeLimit)
                ? Math.Clamp(chargeLimit, MINIMUM_CHARGE_LIMIT, MAXIMUM_CHARGE_LIMIT)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private void PersistChargeLimit(int chargeLimit)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_chargeLimitConfigPath)!);
            File.WriteAllText(_chargeLimitConfigPath, chargeLimit.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Battery", "Could not save the battery charge limit", exception);
        }
    }

    private async Task ApplyChargeLimitAsync(int chargeLimit)
    {
        var path = Path.Combine("/sys/class/power_supply", device, "charge_control_end_threshold");
        try
        {
            await File.WriteAllTextAsync(path, chargeLimit.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Battery", $"Could not set charge limit through {path}", exception);
        }
    }

    internal void SetPowerProfile(string profile)
    {
        var powerProfiles = Snapshot.PowerProfiles;
        if (!powerProfiles.Available ||
            !powerProfiles.Profiles.Contains(profile, StringComparer.Ordinal))
        {
            return;
        }

        _powerProfiles = powerProfiles with { Active = profile };
        Snapshot = Snapshot with { PowerProfiles = _powerProfiles };
        _ = ApplyPowerProfileAsync(profile);
    }

    private async Task ApplyPowerProfileAsync(string profile)
    {
        await CommandRunner.TryRunAsync(
            "powerprofilesctl",
            ["set", profile],
            TimeSpan.FromSeconds(2),
            CancellationToken.None);
        _powerProfilesObservedAtUtc = DateTime.MinValue;
    }

    internal static PowerProfileSnapshot ParsePowerProfiles(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return PowerProfileSnapshot.Empty;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        var active = "";
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            var isActive = trimmed.StartsWith('*');
            var profile = trimmed.TrimStart('*').Trim().TrimEnd(':');
            if (profile is not ("power-saver" or "balanced" or "performance"))
            {
                continue;
            }

            found.Add(profile);
            if (isActive)
            {
                active = profile;
            }
        }

        string[] preferredOrder = ["power-saver", "balanced", "performance"];
        var profiles = preferredOrder.Where(found.Contains).ToArray();
        return profiles.Length == 0
            ? PowerProfileSnapshot.Empty
            : new PowerProfileSnapshot(true, active, profiles);
    }

    private static string Read(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetChargeLimitConfigPath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configRoot, "hyprnetshell", "battery-charge-limit");
    }
}

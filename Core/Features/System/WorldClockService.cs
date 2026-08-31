using System.Text.Json;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

internal sealed class WorldClockService
{
    private static readonly string[] DefaultTimeZoneIds =
    [
        "UTC",
        "Europe/Kyiv",
        "Asia/Tel_Aviv",
        "America/New_York",
        "America/Los_Angeles",
        "Asia/Tokyo",
    ];

    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _persistLock = new(1, 1);
    private readonly string _configPath;
    private readonly IReadOnlyDictionary<string, WorldClock> _clocksById;
    private WorldClock[] _selectedClocks;

    public static WorldClockService Shared { get; } = new();

    public IReadOnlyList<WorldClock> AvailableClocks { get; }

    public IReadOnlyList<WorldClock> SelectedClocks
    {
        get
        {
            lock (_stateLock)
            {
                return _selectedClocks;
            }
        }
    }

    public WorldClockService(string? configPath = null)
    {
        _configPath = configPath ?? GetConfigPath();
        AvailableClocks = TimeZoneInfo.GetSystemTimeZones()
            .Select(zone => new WorldClock(zone.Id, BuildDisplayName(zone.Id), zone))
            .OrderBy(clock => clock.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(clock => clock.TimeZoneId, StringComparer.Ordinal)
            .ToArray();
        _clocksById = AvailableClocks.ToDictionary(clock => clock.TimeZoneId, StringComparer.Ordinal);
        _selectedClocks = LoadSelectedClocks();
    }

    public bool IsSelected(string timeZoneId)
    {
        lock (_stateLock)
        {
            return _selectedClocks.Any(clock => clock.TimeZoneId == timeZoneId);
        }
    }

    public void Toggle(string timeZoneId)
    {
        if (!_clocksById.TryGetValue(timeZoneId, out var clock))
        {
            return;
        }

        lock (_stateLock)
        {
            var existingIndex = Array.FindIndex(
                _selectedClocks,
                selected => selected.TimeZoneId == timeZoneId);
            _selectedClocks = existingIndex >= 0
                ? _selectedClocks.Where((_, index) => index != existingIndex).ToArray()
                : [.. _selectedClocks, clock];
        }

        _ = PersistLatestAsync();
    }

    public static DateTime GetTime(WorldClock clock, DateTime utcNow)
    {
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utcNow, clock.TimeZone);
        }
        catch (ArgumentException)
        {
            return utcNow;
        }
    }

    private WorldClock[] LoadSelectedClocks()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var document = JsonSerializer.Deserialize(
                    File.ReadAllText(_configPath),
                    WorldClockJsonContext.Default.WorldClockConfigurationDocument);
                if (document is not null)
                {
                    return ResolveClocks(document.TimeZoneIds);
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("WorldClocks", "Could not load world clock configuration; using defaults", exception);
        }

        return ResolveClocks(DefaultTimeZoneIds);
    }

    private WorldClock[] ResolveClocks(IEnumerable<string>? timeZoneIds)
    {
        return timeZoneIds?
            .Distinct(StringComparer.Ordinal)
            .Select(id => _clocksById.GetValueOrDefault(id))
            .Where(clock => clock is not null)
            .Cast<WorldClock>()
            .ToArray() ?? [];
    }

    private async Task PersistLatestAsync()
    {
        await _persistLock.WaitAsync();
        try
        {
            string[] selectedIds;
            lock (_stateLock)
            {
                selectedIds = _selectedClocks.Select(clock => clock.TimeZoneId).ToArray();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            await File.WriteAllTextAsync(
                _configPath,
                JsonSerializer.Serialize(
                    new WorldClockConfigurationDocument(selectedIds),
                    WorldClockJsonContext.Default.WorldClockConfigurationDocument));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("WorldClocks", "Could not save world clock configuration", exception);
        }
        finally
        {
            _persistLock.Release();
        }
    }

    private static string BuildDisplayName(string timeZoneId)
    {
        if (timeZoneId is "UTC" or "Etc/UTC")
        {
            return "UTC";
        }

        var separator = timeZoneId.LastIndexOf('/');
        return timeZoneId[(separator + 1)..].Replace('_', ' ');
    }

    private static string GetConfigPath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configRoot, "hyprnetshell", "world-clocks.json");
    }
}

using HyprNetShell.Core.Configuration;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

internal sealed class WorldClockService
{


    private readonly Lock _stateLock = new();
    private readonly AppConfigurationStore _configuration = AppConfigurationStore.Shared;
    private readonly IReadOnlyDictionary<string, WorldClock> _clocksById;
    private WorldClock[] _selectedClocks;

    public static WorldClockService Shared { get; } = new();

    public IReadOnlyList<WorldClock> AvailableClocks
    {
        get;
    }

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

    public WorldClockService()
    {
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

        PersistLatest();
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
        return ResolveClocks(_configuration.Snapshot.WorldClocks.TimeZoneIds);
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

    private void PersistLatest()
    {
        List<string> selectedIds;
        lock (_stateLock)
        {
            selectedIds = _selectedClocks.Select(clock => clock.TimeZoneId).ToList();
        }

        _configuration.Update(config => config.WorldClocks.TimeZoneIds = selectedIds);
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


}

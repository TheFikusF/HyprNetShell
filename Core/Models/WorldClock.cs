namespace HyprNetShell.Core.Models;

internal sealed record WorldClock(string TimeZoneId, string DisplayName, TimeZoneInfo TimeZone);

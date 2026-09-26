using System.Collections.Immutable;

namespace HyprNetShell.Core.Models;

internal enum CalendarSourceState
{
    Pending,
    Success,
    Error,
}

internal sealed record CalendarOccurrence(
    string Title,
    DateTime Start,
    DateTime End,
    bool IsAllDay,
    string? Location,
    string SourceUrl);

internal sealed record CalendarSourceStatus(
    string SourceUrl,
    CalendarSourceState State,
    string? Error,
    DateTime? UpdatedAt);

internal sealed record GoogleCalendarSource(
    string Id,
    string Name,
    bool Primary,
    bool Hidden,
    bool Enabled);

internal sealed record CalendarSnapshot(
    ImmutableArray<CalendarOccurrence> Occurrences,
    ImmutableArray<CalendarSourceStatus> Sources,
    DateTime RangeStart,
    DateTime RangeEnd,
    DateTime? UpdatedAt)
{
    internal static CalendarSnapshot Empty { get; } = new(
        ImmutableArray<CalendarOccurrence>.Empty,
        ImmutableArray<CalendarSourceStatus>.Empty,
        DateTime.MinValue,
        DateTime.MinValue,
        null);
}

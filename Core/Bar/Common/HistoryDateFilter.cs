namespace HyprNetShell.Core.Bar.Common;

internal enum HistoryDateRange
{
    AllTime,
    Today,
    PastWeek,
    PastMonth,
}

internal static class HistoryDateFilter
{
    public static IReadOnlyList<string> Labels { get; } =
    [
        "All time",
        "Today",
        "Past week",
        "Past month",
    ];

    public static bool Includes(HistoryDateRange range, DateTime timestamp, DateTime? now = null)
    {
        if (range == HistoryDateRange.AllTime)
        {
            return true;
        }

        var localTimestamp = timestamp.Kind == DateTimeKind.Local ? timestamp : timestamp.ToLocalTime();
        var localNow = (now ?? DateTime.Now).ToLocalTime();
        return range switch
        {
            HistoryDateRange.Today => localTimestamp.Date == localNow.Date,
            HistoryDateRange.PastWeek => localTimestamp >= localNow.AddDays(-7),
            HistoryDateRange.PastMonth => localTimestamp >= localNow.AddMonths(-1),
            _ => true,
        };
    }
}

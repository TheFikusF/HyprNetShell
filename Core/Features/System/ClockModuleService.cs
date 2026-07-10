using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class ClockModuleService : IBarDataService
{
    public ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        state.AddCenterModule(new BarModuleSnapshot(
            "clock-date",
            now.ToString("ddd dd, MMM"),
            Popup: BuildCalendarPopup(now)));
        state.AddCenterModule(new BarModuleSnapshot(
            "clock-time",
            now.ToString("HH:mm"),
            Popup: new PopupSnapshot("world-clock", "World Clocks", [
                new PopupRowSnapshot($"Local - {now:HH:mm}", PopupRowKind.Header),
                new PopupRowSnapshot($"UTC - {DateTime.UtcNow:HH:mm}"),
            ])));
        return ValueTask.CompletedTask;
    }

    private static PopupSnapshot BuildCalendarPopup(DateTime now)
    {
        var first = new DateTime(now.Year, now.Month, 1);
        var days = DateTime.DaysInMonth(now.Year, now.Month);
        var rows = new List<PopupRowSnapshot>
        {
            new(first.ToString("MMMM yyyy"), PopupRowKind.Header),
            new("Mon Tue Wed Thu Fri Sat Sun", PopupRowKind.Header),
        };

        var offset = ((int)first.DayOfWeek + 6) % 7;
        var cells = Enumerable.Repeat("  ", offset)
            .Concat(Enumerable.Range(1, days).Select(day => day == now.Day ? $"[{day:00}]" : $"{day:00}"))
            .ToArray();

        foreach (var week in cells.Chunk(7))
        {
            rows.Add(new PopupRowSnapshot(string.Join(" ", week)));
        }

        return new PopupSnapshot("calendar", "Calendar", rows);
    }
}

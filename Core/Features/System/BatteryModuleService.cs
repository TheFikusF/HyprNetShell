using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class BatteryModuleService(string device = "BAT0") : IBarDataService
{
    public ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        var basePath = Path.Combine("/sys/class/power_supply", device);
        var capacityText = Read(Path.Combine(basePath, "capacity"));
        if (!int.TryParse(capacityText, out var capacity))
        {
            state.Battery = BatterySnapshot.Empty;
            return ValueTask.CompletedTask;
        }

        var status = Read(Path.Combine(basePath, "status"));
        state.Battery = new BatterySnapshot(
            true,
            device,
            Math.Clamp(capacity, 0, 100),
            string.IsNullOrWhiteSpace(status) ? "Unknown" : status);

        return ValueTask.CompletedTask;
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
}

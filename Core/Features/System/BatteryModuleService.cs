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
            return ValueTask.CompletedTask;
        }

        var status = Read(Path.Combine(basePath, "status"));
        var moduleState = capacity <= 15 && !status.Equals("Charging", StringComparison.OrdinalIgnoreCase)
            ? ModuleState.Critical
            : status.Equals("Charging", StringComparison.OrdinalIgnoreCase)
                ? ModuleState.Active
                : ModuleState.Normal;

        state.AddRightModule(new BarModuleSnapshot(
            "battery",
            $"BAT {capacity}%",
            moduleState,
            new PopupSnapshot("battery", "Battery", [
                new PopupRowSnapshot($"Device: {device}", PopupRowKind.Header),
                new PopupRowSnapshot($"Capacity: {capacity}%"),
                new PopupRowSnapshot($"Status: {(string.IsNullOrWhiteSpace(status) ? "Unknown" : status)}"),
            ]),
            Percentage: Math.Clamp(capacity, 0, 100)));

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

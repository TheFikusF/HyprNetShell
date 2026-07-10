namespace HyprNetShell.Core.Models;

public sealed record BarModuleSnapshot(
    string Id,
    string Label,
    ModuleState State = ModuleState.Normal,
    PopupSnapshot? Popup = null,
    string? ImagePath = null,
    int? Percentage = null);

public enum ModuleState
{
    Normal,
    Active,
    Warning,
    Critical,
    Muted,
}

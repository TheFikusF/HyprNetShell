namespace HyprNetShell.Core.Models;

public sealed record HyprlandSnapshot(
    IReadOnlyList<WorkspaceSnapshot> Workspaces,
    IReadOnlyList<MonitorWorkspaceSnapshot> MonitorWorkspaces,
    string FocusedTitle,
    string FocusedClassName,
    int FocusedWorkspaceId,
    string KeyboardName,
    string LayoutName,
    bool Available)
{
    public static HyprlandSnapshot Empty { get; } = new([], [], "Desktop", "", 1, "", "", false);
}

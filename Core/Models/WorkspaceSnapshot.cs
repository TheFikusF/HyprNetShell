namespace HyprNetShell.Core.Models;

public sealed record WorkspaceSnapshot(
    int Id,
    string MonitorName,
    bool Active,
    IReadOnlyList<WindowSummary> Windows,
    PopupSnapshot Popup);

public sealed record WindowSummary(
    string Address,
    string ClassName,
    string InitialClassName,
    string Title);

public sealed record MonitorWorkspaceSnapshot(
    string Name,
    bool Current,
    int ActiveWorkspaceId,
    IReadOnlyList<WorkspaceSnapshot> Workspaces);

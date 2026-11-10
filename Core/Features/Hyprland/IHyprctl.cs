namespace HyprNetShell.Core.Features.Hyprland;

internal interface IHyprctl : IDisposable
{
    Task<bool> LaunchDesktopEntryAsync(string desktopFile, CancellationToken cancellationToken = default);
    Task<bool> LaunchDesktopActionAsync(
        string desktopFile,
        string actionId,
        CancellationToken cancellationToken = default);
    Task<bool> FocusWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<bool> OpenWorkspaceOnMonitorAsync(int workspaceId, string monitorName, CancellationToken cancellationToken = default);
    Task<bool> FocusWindowAsync(string windowAddress, CancellationToken cancellationToken = default);
    Task<bool> ExitSessionAsync(CancellationToken cancellationToken = default);
    Task<bool> Bind(
        string keys,
        Action callback,
        HyprlandBindOptions options = default,
        CancellationToken cancellationToken = default);
    Task<bool> SwitchKeyboardLayoutAsync(
        string keyboardName,
        int? layoutIndex = null,
        CancellationToken cancellationToken = default);
}

internal readonly record struct HyprlandBindOptions(bool Release = false, bool Transparent = false);

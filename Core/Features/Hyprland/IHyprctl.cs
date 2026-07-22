namespace HyprNetShell.Core.Features.Hyprland;

internal interface IHyprctl : IDisposable
{
    Task<bool> LaunchDesktopEntryAsync(string desktopFile, CancellationToken cancellationToken = default);
    Task<bool> LaunchDesktopActionAsync(
        string desktopFile,
        string actionId,
        CancellationToken cancellationToken = default);
    Task<bool> FocusWorkspaceAsync(int workspaceId, CancellationToken cancellationToken = default);
    Task<bool> FocusWindowAsync(string windowAddress, CancellationToken cancellationToken = default);
    Task<bool> Bind(
        string keys,
        Action callback,
        HyprlandBindOptions options = default,
        CancellationToken cancellationToken = default);
    Task<bool> SwitchKeyboardLayoutAsync(
        string keyboardName,
        int? layoutIndex = null,
        CancellationToken cancellationToken = default);
    Task<bool> SetWallpaperAsync(string path, CancellationToken cancellationToken = default);
    Task<int?> GetColorTemperatureAsync(CancellationToken cancellationToken = default);
    Task<bool> SetColorTemperatureAsync(int temperatureKelvin, CancellationToken cancellationToken = default);
}

internal readonly record struct HyprlandBindOptions(bool Release = false, bool Transparent = false);

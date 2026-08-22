using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Bar;

public sealed class StatusBarServices : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(700);

    private readonly List<IBarDataService> _dataServices;
    private readonly CancellationTokenSource _lifetime = new();
    private DateTime _lastRefresh = DateTime.MinValue;
    private Task? _refreshTask;
    private bool _disposed;

    internal IHyprctl Hyprctl { get; }
    internal HyprlandService Hyprland { get; }
    internal SuperKeyStateService SuperKey { get; }
    internal NotificationService Notifications { get; }
    internal MusicModuleService Music { get; }
    internal ClipboardHistoryService ClipboardHistory { get; }
    internal WallpaperModuleService Wallpapers { get; }
    internal SniTrayService Tray { get; }
    internal DisplayControlsModuleService DisplayControls { get; }
    internal NetworkModuleService Network { get; }
    internal AudioModuleService Audio { get; }
    internal BluetoothModuleService Bluetooth { get; }
    internal BatteryModuleService Battery { get; }
    internal SystemStatsModuleService SystemStats { get; }
    internal WeatherWidget Weather { get; }
    public MainDialog MainDialog { get; }

    public string? FocusedMonitorName =>
        Hyprland.Snapshot.MonitorWorkspaces.FirstOrDefault(monitor => monitor.Current)?.Name;

    public StatusBarServices()
    {
        Hyprctl = new Hyprctl();
        Hyprland = new HyprlandService();
        Notifications = new NotificationService(Hyprland, Hyprctl);
        SuperKey = new SuperKeyStateService(Hyprctl);
        DisplayControls = new DisplayControlsModuleService(Hyprctl);
        Wallpapers = new WallpaperModuleService(Hyprctl);
        Network = new NetworkModuleService();
        Audio = new AudioModuleService();
        Bluetooth = new BluetoothModuleService();
        Battery = new BatteryModuleService();
        SystemStats = new SystemStatsModuleService();
        Weather = new WeatherWidget(Theme.Default);
        Music = new MusicModuleService();
        ClipboardHistory = new ClipboardHistoryService();
        Tray = new SniTrayService();
        MainDialog = new MainDialog(ClipboardHistory, Hyprctl, Wallpapers, Theme.Default);

        _dataServices =
        [
            Network,
            Audio,
            DisplayControls,
            Bluetooth,
            Battery,
            SystemStats,
            Tray,
        ];
    }

    public void RefreshState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_refreshTask is { IsCompleted: false } || DateTime.UtcNow - _lastRefresh < RefreshInterval)
        {
            return;
        }

        _lastRefresh = DateTime.UtcNow;
        _refreshTask = RefreshStateAsync(_lifetime.Token);
    }

    public bool ConsumeLauncherToggleRequested()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SuperKey.ConsumeLauncherToggleRequested();
    }

    private async Task RefreshStateAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await Task.WhenAll(_dataServices.Select(service => service.RefreshAsync(timeout.Token).AsTask()));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Warning("StatusBar", "Bar service refresh timed out; keeping existing service state");
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("StatusBar", "Could not refresh bar services; keeping their previous state", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        try
        {
            if (_refreshTask?.Wait(TimeSpan.FromMilliseconds(2500)) == false)
            {
                AppLogger.Warning("StatusBar", "Bar service refresh did not stop before shutdown");
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("StatusBar", "Bar service refresh did not stop cleanly", exception);
        }

        MainDialog.Dispose();
        Tray.Dispose();
        ClipboardHistory.Dispose();
        Music.Dispose();
        Wallpapers.Dispose();
        SuperKey.Dispose();
        Notifications.Dispose();
        Hyprland.Dispose();
        Hyprctl.Dispose();
        _lifetime.Dispose();
    }
}

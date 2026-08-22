using System.Diagnostics;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Bar;

public sealed class StatusBarServices : IDisposable
{
    private static readonly TimeSpan FastSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TrayRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AudioFallbackInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(15);

    private readonly List<ScheduledService> _scheduledServices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IBarDataService> _dueServicesBuffer = [];
    private Task? _refreshTask;
    private bool _disposed;

    internal IHyprctl Hyprctl { get; }
    internal HyprlandService Hyprland { get; }
    internal KeyStateService SuperKey { get; }
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

    public string? FocusedMonitorName => Hyprland.Snapshot.MonitorWorkspaces
        .FirstOrDefault(monitor => monitor.Current)?.Name;

    public StatusBarServices()
    {
        Hyprctl = new Hyprctl();
        Hyprland = new HyprlandService();
        Notifications = new NotificationService(Hyprland, Hyprctl);
        SuperKey = new KeyStateService(Hyprctl);
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

        _scheduledServices =
        [
            new(Network, RecoveryInterval),
            new(Audio, AudioFallbackInterval),
            new(DisplayControls, FastSampleInterval),
            new(Bluetooth, RecoveryInterval),
            new(Battery, RecoveryInterval),
            new(SystemStats, FastSampleInterval),
            new(Tray, TrayRefreshInterval),
        ];
    }

    public void RefreshState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_refreshTask is { IsCompleted: false })
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _dueServicesBuffer.Clear();
        foreach (var scheduled in _scheduledServices.Where(x => x.TrySchedule(now)))
        {
            _dueServicesBuffer.Add(scheduled.Service);
        }

        if (_dueServicesBuffer.Count > 0)
        {
            _refreshTask = RefreshStateAsync(_lifetime.Token);
        }
    }

    public bool ConsumeLauncherToggleRequested()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SuperKey.ConsumeLauncherToggleRequested();
    }

    private async Task RefreshStateAsync(
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            var refreshTasks = new Task[_dueServicesBuffer.Count];
            for (var index = 0; index < _dueServicesBuffer.Count; index++)
            {
                refreshTasks[index] = _dueServicesBuffer[index].RefreshAsync(timeout.Token).AsTask();
            }

            await Task.WhenAll(refreshTasks);
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

    private sealed class ScheduledService(IBarDataService service, TimeSpan interval)
    {
        private readonly long _intervalTicks = Math.Max(
            1,
            (long)Math.Ceiling(interval.TotalSeconds * Stopwatch.Frequency));
        private long _nextRefreshTimestamp;

        public IBarDataService Service { get; } = service;

        public bool TrySchedule(long timestamp)
        {
            if (timestamp < _nextRefreshTimestamp)
            {
                return false;
            }

            _nextRefreshTimestamp = timestamp > long.MaxValue - _intervalTicks
                ? long.MaxValue
                : timestamp + _intervalTicks;
            return true;
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
        Battery.Dispose();
        Bluetooth.Dispose();
        Audio.Dispose();
        Network.Dispose();
        Wallpapers.Dispose();
        SuperKey.Dispose();
        Notifications.Dispose();
        Hyprland.Dispose();
        Hyprctl.Dispose();
        _lifetime.Dispose();
    }
}

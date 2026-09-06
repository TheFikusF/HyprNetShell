using System.Diagnostics;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Services;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar;

public sealed class StatusBarServices : IDisposable
{
    private sealed class ScheduledService(IBarDataService service, TimeSpan interval)
    {
        private readonly long _intervalTicks = Math.Max(1, (long)Math.Ceiling(interval.TotalSeconds * Stopwatch.Frequency));
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

    private static readonly TimeSpan FastSampleInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TrayRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AudioFallbackInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(15);

    private readonly IReadOnlyCollection<ScheduledService> _scheduledServices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IBarDataService> _dueServicesBuffer = [];
    private Task? _refreshTask;
    private bool _connectionNotificationsInitialized;
    private bool _lastNetworkConnected;
    private string _lastNetworkConnection = "";
    private HashSet<string> _lastConnectedBluetoothDevices = new(StringComparer.OrdinalIgnoreCase);
    private bool _batteryNotificationInitialized;
    private bool _batteryWasCritical;
    private int _lockScreenRequested;
    private bool _disposed;

    internal HistoryStore History { get; }
    internal TabsService Tabs { get; }
    internal CompositeWindowConfiguration CompositeWindowConfiguration { get; }
    internal CompositeWindowService CompositeWindows { get; }
    internal IHyprctl Hyprctl { get; }
    internal HyprlandService Hyprland { get; }
    internal KeyStateService SuperKey { get; }
    internal NotificationService Notifications { get; }
    internal ScreenshotService Screenshots { get; }
    internal MusicModuleService Music { get; }
    internal ClipboardHistoryService ClipboardHistory { get; }
    internal WallpaperModuleService Wallpapers { get; }
    internal SniTrayService Tray { get; }
    internal DisplayControlsModuleService DisplayControls { get; }
    internal NetworkModuleService Network { get; }
    internal AudioModuleService Audio { get; }
    internal PrivacyModuleService Privacy { get; }
    internal BluetoothModuleService Bluetooth { get; }
    internal BatteryModuleService Battery { get; }
    internal SystemStatsModuleService SystemStats { get; }
    internal WeatherService Weather { get; }
    internal DictionaryService Dictionary { get; }

    public DialogService Dialogs { get; }

    public string? FocusedMonitorName => Hyprland.Snapshot.MonitorWorkspaces
        .FirstOrDefault(monitor => monitor.Current)?.Name;

    public StatusBarServices()
    {
        History = new HistoryStore();
        Hyprctl = new Hyprctl();
        Hyprland = new HyprlandService();
        Notifications = new NotificationService(Hyprland, Hyprctl, History);
        Screenshots = new ScreenshotService(Hyprctl);
        SuperKey = new KeyStateService(Hyprctl);
        DisplayControls = new DisplayControlsModuleService(Hyprctl);
        Wallpapers = new WallpaperModuleService(Hyprctl);
        Network = new NetworkModuleService();
        Audio = new AudioModuleService();
        Privacy = new PrivacyModuleService(Audio);
        Bluetooth = new BluetoothModuleService();
        Battery = new BatteryModuleService();
        SystemStats = new SystemStatsModuleService();
        Weather = new WeatherService();
        Dictionary = new DictionaryService();
        Music = new MusicModuleService();
        ClipboardHistory = new ClipboardHistoryService(History);
        Tray = new SniTrayService();

        Dialogs = new DialogService();
        Tabs = new TabsService(
            ClipboardHistory,
            Hyprctl,
            Network,
            Bluetooth,
            Wallpapers,
            Weather,
            Dictionary,
            Dialogs.Close,
            Theme.Default);
        CompositeWindowConfiguration = new CompositeWindowConfiguration(Tabs.Tabs);
        Dialogs.Register(new CompositeWindow(Theme.Default));
        CompositeWindows = new CompositeWindowService(
            CompositeWindowConfiguration,
            Tabs,
            Dialogs,
            Hyprctl);

        Dialogs.Register(new SettingsDialog(
            this,
            CompositeWindowConfiguration,
            Tabs,
            tabs => Dialogs.Open<CompositeWindow>(tabs),
            Theme.Default));

        _scheduledServices =
        [
            new(Network, RecoveryInterval),
            new(Audio, AudioFallbackInterval),
            new(Privacy, FastSampleInterval),
            new(DisplayControls, FastSampleInterval),
            new(Bluetooth, RecoveryInterval),
            new(Battery, RecoveryInterval),
            new(SystemStats, FastSampleInterval),
            new(Tray, TrayRefreshInterval),
        ];
    }

    internal void RequestLockScreen() => Interlocked.Exchange(ref _lockScreenRequested, 1);

    public bool TryTakeLockScreenRequest() => Interlocked.Exchange(ref _lockScreenRequested, 0) != 0;

    public bool TryTakeScreenshotRequest(out ScreenshotMode mode) => Screenshots.TryTakeRequest(out mode);

    public void ShowShellNotification(
        string title,
        string body,
        string iconName = "",
        bool storeInHistory = false,
        EncodedImageData? image = null,
        bool showImageAsPreview = false) =>
        Notifications.ShowLocal(title, body, iconName, storeInHistory, image, showImageAsPreview);

    public void RefreshState()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckConnectionNotifications();
        CheckBatteryNotification();
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



    private void CheckConnectionNotifications()
    {
        var network = Network.Snapshot;
        var connectedBluetooth = Bluetooth.Snapshot.Devices
            .Where(device => device.Connected)
            .ToDictionary(device => device.Address, device => device.Name, StringComparer.OrdinalIgnoreCase);

        if (!_connectionNotificationsInitialized)
        {
            if (!network.WifiAvailable && !Bluetooth.Snapshot.Available)
            {
                return;
            }

            _connectionNotificationsInitialized = true;
            _lastNetworkConnected = network.Connected;
            _lastNetworkConnection = network.Connection;
            _lastConnectedBluetoothDevices = connectedBluetooth.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return;
        }

        if (network.Connected && (!_lastNetworkConnected ||
            !string.Equals(network.Connection, _lastNetworkConnection, StringComparison.Ordinal)))
        {
            Notifications.ShowLocal("Network connected", network.Connection, "wifi", storeInHistory: false);
        }
        else if (!network.Connected && _lastNetworkConnected)
        {
            Notifications.ShowLocal("Network disconnected", _lastNetworkConnection, "wifi-off", storeInHistory: false);
        }

        foreach (var device in connectedBluetooth.Where(device => !_lastConnectedBluetoothDevices.Contains(device.Key)))
        {
            Notifications.ShowLocal("Bluetooth connected", device.Value, "bluetooth-connected", storeInHistory: false);
        }
        foreach (var address in _lastConnectedBluetoothDevices.Where(address => !connectedBluetooth.ContainsKey(address)))
        {
            Notifications.ShowLocal("Bluetooth disconnected", address, "bluetooth-off", storeInHistory: false);
        }

        _lastNetworkConnected = network.Connected;
        _lastNetworkConnection = network.Connection;
        _lastConnectedBluetoothDevices = connectedBluetooth.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private void CheckBatteryNotification()
    {
        var battery = Battery.Snapshot;
        if (!battery.Available)
        {
            return;
        }

        if (!_batteryNotificationInitialized)
        {
            _batteryNotificationInitialized = true;
            _batteryWasCritical = false;
        }

        if (battery.IsCritical && !_batteryWasCritical)
        {
            Notifications.ShowLocal(
                "Low battery",
                $"Battery is at {battery.Percentage}%.",
                "battery-warning",
                storeInHistory: false);
        }

        _batteryWasCritical = battery.IsCritical;
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
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning("StatusBar", "Bar service refresh timed out; keeping existing service state");
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

        CompositeWindows.Dispose();
        Dialogs.Dispose();
        Tabs.Dispose();
        Tray.Dispose();
        ClipboardHistory.Dispose();
        Music.Dispose();
        Weather.Dispose();
        Battery.Dispose();
        Bluetooth.Dispose();
        Audio.Dispose();
        Network.Dispose();
        Wallpapers.Dispose();
        SuperKey.Dispose();
        Screenshots.Dispose();
        Notifications.Dispose();
        Hyprland.Dispose();
        Hyprctl.Dispose();
        History.Dispose();
        _lifetime.Dispose();
    }
}

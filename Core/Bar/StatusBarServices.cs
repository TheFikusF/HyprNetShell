using System.Diagnostics;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Configuration;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.KdeConnect;
using HyprNetShell.Core.Features.OnlineAccounts;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.Spotify;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Overview;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar;

public sealed class StatusBarServices : IDisposable
{
    private sealed class ScheduledService(
        IBarDataService service,
        TimeSpan interval,
        TimeSpan? timeout = null)
    {
        private readonly long _intervalTicks = Math.Max(1, (long)Math.Ceiling(interval.TotalSeconds * Stopwatch.Frequency));
        private long _nextRefreshTimestamp;

        public IBarDataService Service { get; } = service;
        public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(2);

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
    private static readonly TimeSpan CalendarRefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan ChatGptUsageRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IReadOnlyCollection<ScheduledService> _scheduledServices;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<ScheduledService> _dueServicesBuffer = [];
    private Task? _refreshTask;
    private bool _connectionNotificationsInitialized;
    private bool _lastNetworkConnected;
    private string _lastNetworkConnection = "";
    private HashSet<string> _lastConnectedBluetoothDevices = new(StringComparer.OrdinalIgnoreCase);

    private BluetoothSnapshot? _lastBluetoothSnapshot;
    private int _lockScreenRequested;
    private bool _disposed;

    internal HistoryStore History
    {
        get;
    }
    internal TabsService Tabs
    {
        get;
    }
    internal CompositeWindowConfiguration CompositeWindowConfiguration
    {
        get;
    }
    internal CompositeWindowService CompositeWindows
    {
        get;
    }
    internal IHyprctl Hyprctl
    {
        get;
    }
    internal HyprlandService Hyprland
    {
        get;
    }
    internal UrlLauncher UrlLauncher
    {
        get;
    }

    internal NotificationService Notifications
    {
        get;
    }
    internal ScreenshotService Screenshots
    {
        get;
    }
    internal MusicModuleService Music
    {
        get;
    }
    internal SpotifyPlaybackService Spotify
    {
        get;
    }
    internal ClipboardHistoryService ClipboardHistory
    {
        get;
    }
    internal KdeConnectService KdeConnect
    {
        get;
    }
    internal WallpaperModuleService Wallpapers
    {
        get;
    }
    internal SniTrayService Tray
    {
        get;
    }
    internal DisplayControlsModuleService DisplayControls
    {
        get;
    }
    internal NetworkModuleService Network
    {
        get;
    }
    private PipeWireGraphService PipeWireGraph
    {
        get;
    }
    internal AudioModuleService Audio
    {
        get;
    }
    internal PrivacyModuleService Privacy
    {
        get;
    }
    internal BluetoothModuleService Bluetooth
    {
        get;
    }
    internal BatteryModuleService Battery
    {
        get;
    }
    internal DeviceBatteryService DeviceBatteries
    {
        get;
    }
    internal SystemStatsModuleService SystemStats
    {
        get;
    }
    internal WeatherService Weather
    {
        get;
    }
    internal CalendarService Calendar
    {
        get;
    }
    internal DictionaryService Dictionary
    {
        get;
    }
    internal OnlineAccountsService OnlineAccounts
    {
        get;
    }
    internal ChatGptUsageService ChatGptUsage
    {
        get;
    }

    public DialogService Dialogs
    {
        get;
    }

    public string? FocusedMonitorName => Hyprland.Snapshot.MonitorWorkspaces
        .FirstOrDefault(monitor => monitor.Current)?.Name;

    public IWindowThumbnailService WindowThumbnails
    {
        get;
    }

    public OverviewController Overview
    {
        get;
    }

    public StatusBarServices(IWindowThumbnailBackend? windowThumbnailBackend = null)
    {
        WindowThumbnails = new WindowThumbnailService(windowThumbnailBackend);
        History = new HistoryStore();
        Hyprctl = new Hyprctl();
        Hyprland = new HyprlandService();

        Dialogs = new DialogService();
        UrlLauncher = new UrlLauncher(Hyprland, Hyprctl, Dialogs.RequestClose);
        Notifications = new NotificationService(Hyprland, Hyprctl, History);
        Screenshots = new ScreenshotService(Hyprctl);

        DisplayControls = new DisplayControlsModuleService(Hyprctl);
        Wallpapers = new WallpaperModuleService(Hyprctl);
        Network = new NetworkModuleService();
        PipeWireGraph = new PipeWireGraphService();
        Audio = new AudioModuleService(PipeWireGraph);
        Privacy = new PrivacyModuleService(Audio, PipeWireGraph);
        Bluetooth = new BluetoothModuleService();
        Battery = new BatteryModuleService();
        SystemStats = new SystemStatsModuleService();
        Weather = new WeatherService(UrlLauncher);
        OnlineAccounts = new OnlineAccountsService(UrlLauncher);
        Calendar = new CalendarService(OnlineAccounts);
        Dictionary = new DictionaryService();
        ChatGptUsage = new ChatGptUsageService(OnlineAccounts);
        Spotify = new SpotifyPlaybackService(OnlineAccounts);
        Music = new MusicModuleService(Spotify);
        ClipboardHistory = new ClipboardHistoryService(History);
        Overview = new OverviewController(Hyprland, Hyprctl, WindowThumbnails, () => Wallpapers.CurrentWallpaper, ClipboardHistory);
        KdeConnect = new KdeConnectService(ClipboardHistory);
        DeviceBatteries = new DeviceBatteryService(Battery, Bluetooth, KdeConnect, Notifications);
        Tray = new SniTrayService();

        Tabs = new TabsService(
            ClipboardHistory,
            KdeConnect,
            Hyprctl,
            UrlLauncher,
            Network,
            Bluetooth,
            Wallpapers,
            Weather,
            Calendar,
            Dictionary,
            Dialogs.Close);
        CompositeWindowConfiguration = new CompositeWindowConfiguration(Tabs.Tabs);
        Dialogs.Register(new CompositeWindow(Tabs.Inputs));
        CompositeWindows = new CompositeWindowService(
            CompositeWindowConfiguration,
            Tabs,
            Dialogs,
            Hyprctl);

        Dialogs.Register(new SettingsDialog(
            this,
            CompositeWindowConfiguration,
            Tabs,
            tabs => Dialogs.Open<CompositeWindow>(tabs)));

        OnlineAccounts.AuthorizationCallbackReceived += HandleAuthorizationCallbackReceived;
        OnlineAccounts.EnsureInitialized();

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
            new(Calendar, CalendarRefreshInterval, TimeSpan.FromSeconds(40)),
            new(ChatGptUsage, ChatGptUsageRefreshInterval, TimeSpan.FromSeconds(10)),
        ];
    }


    private void HandleAuthorizationCallbackReceived()
    {
        if (!_disposed)
        {
            Dialogs.RequestOpen<SettingsDialog>();
        }
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
        DeviceBatteries.Refresh();
        if (_refreshTask is { IsCompleted: false })
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        _dueServicesBuffer.Clear();
        foreach (var scheduled in _scheduledServices.Where(x => x.TrySchedule(now)))
        {
            _dueServicesBuffer.Add(scheduled);
        }

        if (_dueServicesBuffer.Count > 0)
        {
            _refreshTask = RefreshStateAsync(_lifetime.Token);
        }
    }



    private void CheckConnectionNotifications()
    {
        var network = Network.Snapshot;
        var bluetooth = Bluetooth.Snapshot;

        if (!_connectionNotificationsInitialized)
        {
            if (!network.WifiAvailable && !bluetooth.Available)
            {
                return;
            }

            _connectionNotificationsInitialized = true;
            _lastNetworkConnected = network.Connected;
            _lastNetworkConnection = network.Connection;
            _lastConnectedBluetoothDevices = bluetooth.Devices
                .Where(device => device.Connected)
                .Select(device => device.Address)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _lastBluetoothSnapshot = bluetooth;
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

        if (!ReferenceEquals(bluetooth, _lastBluetoothSnapshot))
        {
            var connectedBluetooth = bluetooth.Devices
                .Where(device => device.Connected)
                .ToDictionary(device => device.Address, device => device.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var device in connectedBluetooth.Where(device => !_lastConnectedBluetoothDevices.Contains(device.Key)))
            {
                Notifications.ShowLocal("Bluetooth connected", device.Value, "bluetooth-connected", storeInHistory: false);
            }
            foreach (var address in _lastConnectedBluetoothDevices.Where(address => !connectedBluetooth.ContainsKey(address)))
            {
                Notifications.ShowLocal("Bluetooth disconnected", address, "bluetooth-off", storeInHistory: false);
            }

            _lastConnectedBluetoothDevices = connectedBluetooth.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _lastBluetoothSnapshot = bluetooth;
        }

        _lastNetworkConnected = network.Connected;
        _lastNetworkConnection = network.Connection;

    }


    private async Task RefreshStateAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var refreshTasks = new Task[_dueServicesBuffer.Count];
            for (var index = 0; index < _dueServicesBuffer.Count; index++)
            {
                refreshTasks[index] = RefreshScheduledServiceAsync(_dueServicesBuffer[index], cancellationToken);
            }

            await Task.WhenAll(refreshTasks);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("StatusBar", "Could not refresh bar services; keeping their previous state", exception);
        }
    }

    private static async Task RefreshScheduledServiceAsync(
        ScheduledService scheduled,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(scheduled.Timeout);
        try
        {
            await scheduled.Service.RefreshAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning(
                "StatusBar",
                $"{scheduled.Service.GetType().Name} refresh timed out; keeping existing service state");
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

        OnlineAccounts.AuthorizationCallbackReceived -= HandleAuthorizationCallbackReceived;
        Overview.Dispose();
        WindowThumbnails.Dispose();
        CompositeWindows.Dispose();
        Dialogs.Dispose();
        Tabs.Dispose();
        Tray.Dispose();
        KdeConnect.Dispose();
        ClipboardHistory.Dispose();
        Music.Dispose();
        Spotify.Dispose();
        ChatGptUsage.Dispose();
        Calendar.Dispose();
        OnlineAccounts.Dispose();
        Weather.Dispose();
        Battery.Dispose();
        Bluetooth.Dispose();
        Privacy.Dispose();
        Audio.Dispose();
        PipeWireGraph.Dispose();
        Network.Dispose();
        Wallpapers.Dispose();

        Screenshots.Dispose();
        Notifications.Dispose();
        UrlLauncher.Dispose();
        Hyprland.Dispose();
        Hyprctl.Dispose();
        History.Dispose();
        AppConfigurationStore.Shared.Flush();
        _lifetime.Dispose();
    }
}

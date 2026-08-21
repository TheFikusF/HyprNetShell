using HyprNetShell.Core.Bar.Modules;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Services;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

public interface IDrawableModule
{
    public Node Draw();
}

file class CompositeModule : IDrawableModule
{
    private readonly ICollection<IDrawableModule> _drawableModules;

    public CompositeModule(params ICollection<IDrawableModule> drawableModules)
    {
        _drawableModules = drawableModules;
    }

    public Node Draw() => new BoxNode { Children = [.._drawableModules.Select(x => x.Draw())] };
}

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

public sealed class StatusBar
{
    private readonly int _barHeight;
    private readonly IRenderApi _renderer;
    private readonly NotificationService _notificationService;
    private readonly CenterModule _centerModule;
    private readonly ModulesCommon.PopupCoordinator _popupCoordinator = new();
    private readonly Insets _layoutInsets = new(6, 6, 0, 6);
    private readonly ICollection<IDrawableModule> _leftModules;
    private readonly ICollection<IDrawableModule> _rightModules;

    public StatusBar(StatusBarServices services, IRenderApi renderer, int barHeight, Func<string> getOutputName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(getOutputName);

        _renderer = renderer;
        _barHeight = barHeight;
        _notificationService = services.Notifications;

        var languageModule = new LanguageModule(
            services.Hyprland,
            services.Hyprctl,
            Theme.Default,
            _popupCoordinator);
        var systemStatsModule = new SystemStatsModule(services.SystemStats, Theme.Default, _popupCoordinator);
        var networkModule = new NetworkModule(services.Network, Theme.Default, _popupCoordinator);
        var audioModule = new AudioModule(services.Audio, services.Bluetooth, Theme.Default, _popupCoordinator);
        var displayControlsModule = new DisplayControlsModule(
            services.DisplayControls,
            Theme.Default,
            _popupCoordinator);
        var bluetoothModule = new BluetoothModule(services.Bluetooth, Theme.Default, _popupCoordinator);
        var batteryModule = new BatteryModule(services.Battery, Theme.Default, _popupCoordinator);
        _centerModule = new CenterModule(services.Notifications, services.Weather, Theme.Default, _popupCoordinator);
        var musicModule = new MusicModule(services.Music, Theme.Default, _popupCoordinator);
        var trayModule = new TrayModule(services.Tray, Theme.Default, _popupCoordinator);
        var powerModule = new PowerModule(Theme.Default, _popupCoordinator);
        var workspacesModule = new WorkspacesModule(
            services.Hyprland,
            services.Hyprctl,
            services.SuperKey,
            Theme.Default,
            getOutputName,
            () => languageModule.IsShown,
            _popupCoordinator);

        _leftModules = [workspacesModule, musicModule];
        _rightModules =
        [
            new CompositeModule(audioModule, displayControlsModule, bluetoothModule, networkModule),
            systemStatsModule,
            languageModule,
            batteryModule,
            trayModule,
            powerModule,
        ];
    }



    public void Draw()
    {
        try
        {
            DrawLeftRight();
            DrawCenter();
            DrawNotificationPopups();
        }
        finally
        {
            _popupCoordinator.EndFrame();
        }
    }



    private void DrawLeftRight()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _barHeight, new Style { Padding = _layoutInsets });
        layout.AddNode(new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 6 },
            Children = [.._leftModules.Select(x => x.Draw())],
        });

        layout.AddNode(new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 6 },
            Children = [.._rightModules.Select(x => x.Draw())],
        });
    }

    private void DrawCenter()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _barHeight, new Style { Padding = _layoutInsets });
        layout.AddNode(_centerModule.Draw());
    }

    private void DrawNotificationPopups()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _renderer.Height);
        layout.AddNode(new SpacerNode());
        layout.AddNode(NotificationPopupLayout.Draw(
            _notificationService.Snapshot,
            _notificationService,
            Theme.Default,
            _renderer.Height,
            _barHeight));
    }

}

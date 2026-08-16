using HyprNetShell.Core.Bar.Modules;
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

public sealed class StatusBar : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(700);

    private readonly IHyprctl _hyprctl = new Hyprctl();
    private readonly HyprlandService _hyprland = new();
    private readonly SuperKeyStateService _superKey;
    private readonly int _barHeight;
    private readonly IRenderApi _renderer;

    private readonly NotificationService _notificationService;
    private readonly MusicModuleService _musicService = new();
    private readonly CenterModule _centerModule;
    private readonly ClipboardHistoryService _clipboardHistory = new();
    private readonly WallpaperModuleService _wallpaperService;
    private readonly MainDialog _mainDialog;
    private readonly SniTrayService _trayService = new();

    private readonly List<IBarDataService> _dataServices;

    private readonly Insets _layoutInsets = new (6, 6, 0, 6);

    private DateTime _lastRefresh = DateTime.MinValue;
    private Task? _refreshTask;

    private readonly ICollection<IDrawableModule> _leftModules;
    private readonly ICollection<IDrawableModule> _rightModules;

    public StatusBar(IRenderApi renderer, int barHeight)
    {
        _renderer = renderer;
        _barHeight = barHeight;
        _notificationService = new NotificationService(_hyprland, _hyprctl);
        _superKey = new SuperKeyStateService(_hyprctl);
        var displayControlsService = new DisplayControlsModuleService(_hyprctl);
        _wallpaperService = new WallpaperModuleService(_hyprctl);
        _mainDialog = new MainDialog(_clipboardHistory, _hyprctl, _wallpaperService, Theme.Default);
        var networkService = new NetworkModuleService();
        var audioService = new AudioModuleService();
        var bluetoothService = new BluetoothModuleService();
        var batteryService = new BatteryModuleService();
        var systemStatsService = new SystemStatsModuleService();
        _dataServices =
        [
            networkService,
            audioService,
            displayControlsService,
            bluetoothService,
            batteryService,
            systemStatsService,
            _trayService,
        ];
        var languageModule = new LanguageModule(_hyprland, _hyprctl, Theme.Default);
        var systemStatsModule = new SystemStatsModule(systemStatsService, Theme.Default);
        var networkModule = new NetworkModule(networkService, Theme.Default);
        var audioModule = new AudioModule(audioService, bluetoothService, Theme.Default);
        var displayControlsModule = new DisplayControlsModule(displayControlsService, Theme.Default);
        var bluetoothModule = new BluetoothModule(bluetoothService, Theme.Default);
        var batteryModule = new BatteryModule(batteryService, Theme.Default);
        _centerModule = new CenterModule(_notificationService, Theme.Default);
        var musicModule = new MusicModule(_musicService, Theme.Default);
        var trayModule = new TrayModule(_trayService, Theme.Default);
        var powerModule = new PowerModule(Theme.Default);
        var workspacesModule = new WorkspacesModule(_hyprland, _hyprctl, _superKey, Theme.Default, () => languageModule.IsShown);

        _leftModules = [workspacesModule, musicModule];
        _rightModules =
        [
            new CompositeModule(audioModule, displayControlsModule, bluetoothModule, networkModule),
            systemStatsModule, languageModule, batteryModule, trayModule, powerModule
        ];
    }

    public bool IsMainDialogOpen => _mainDialog.IsOpen;

    public void HandleMainDialogInput(int pressedKey, string textInput, float scrollDelta)
    {
        if (_superKey.ConsumeLauncherToggleRequested())
        {
            _mainDialog.Toggle();
        }

        _mainDialog.HandleInput(pressedKey, textInput, scrollDelta);
    }

    public void Draw()
    {
        RefreshState();

        DrawLeftRight();
        DrawCenter();
        DrawNotificationPopups();
        DrawMainDialog();
    }

    private void DrawMainDialog()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _renderer.Height);
        layout.AddNode(_mainDialog.Draw());
    }

    private void DrawLeftRight()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _barHeight, new Style { Padding = _layoutInsets });
        layout.AddNode(new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 6 },
            Children = [.._leftModules.Select(x => x.Draw())]
        });

        layout.AddNode(new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 6 },
            Children = [.._rightModules.Select(x => x.Draw())]
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

    private void RefreshState()
    {
        if (_refreshTask is { IsCompleted: false } || DateTime.UtcNow - _lastRefresh < RefreshInterval)
        {
            return;
        }

        _lastRefresh = DateTime.UtcNow;
        _refreshTask = RefreshStateAsync();
    }

    private async Task RefreshStateAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await Task.WhenAll(_dataServices.Select(service => service.RefreshAsync(cts.Token).AsTask()));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // The refresh has a fixed time budget. Services retain their previous
            // state when a slower refresh uses it up, without logging an expected cancellation
            // as an exception.
            AppLogger.Warning("StatusBar", "Bar service refresh timed out; keeping existing service state");
        }
        catch (Exception exception)
        {
            // Keep drawing the service-owned state if a transient command fails.
            AppLogger.Warning("StatusBar", "Could not refresh bar services; keeping their previous state", exception);
        }
    }

    public void Dispose()
    {
        _hyprland.Dispose();
        _superKey.Dispose();
        _notificationService.Dispose();
        _clipboardHistory.Dispose();
        _musicService.Dispose();
        _trayService.Dispose();
        _wallpaperService.Dispose();
        _hyprctl.Dispose();
    }
}

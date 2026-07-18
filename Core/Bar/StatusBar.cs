using HyprNetShell.Core.Bar.Modules;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
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
    private readonly DisplayControlsModuleService _displayControlsService;
    private readonly WorkspacesModule _workspacesModule;
    private readonly LanguageModule _languageModule;
    private readonly SystemStatsModule _systemStatsModule;
    private readonly NetworkModule _networkModule;
    private readonly AudioModule _audioModule;
    private readonly DisplayControlsModule _displayControlsModule;
    private readonly BluetoothModule _bluetoothModule;
    private readonly BatteryModule _batteryModule;
    private readonly CenterModule _centerModule;
    private readonly MusicModule _musicModule;
    private readonly TrayModule _trayModule;
    private readonly PowerModule _powerModule;
    private readonly ClipboardHistoryService _clipboardHistory = new();
    private readonly MainDialog _mainDialog;
    private readonly SniTrayService _trayService = new();

    private readonly List<IBarDataService> _dataServices;

    private readonly Insets _layoutInsets = new Insets(6, 6, 0, 6);

    private BarSnapshot _snapshot = BarSnapshot.Empty;
    private DateTime _lastRefresh = DateTime.MinValue;
    private Task? _refreshTask;

    private ICollection<IDrawableModule> _leftModules;
    private ICollection<IDrawableModule> _rightModules;

    public StatusBar(IRenderApi renderer, int barHeight)
    {
        _renderer = renderer;
        _barHeight = barHeight;
        _notificationService = new NotificationService(_hyprland, _hyprctl);
        _superKey = new SuperKeyStateService(_hyprctl);
        _displayControlsService = new DisplayControlsModuleService(_hyprctl);
        _mainDialog = new MainDialog(_clipboardHistory, _hyprctl);
        _dataServices =
        [
            _notificationService,
            new NetworkModuleService(),
            new AudioModuleService(),
            _displayControlsService,
            new BluetoothModuleService(),
            new BatteryModuleService(),
            new SystemStatsModuleService(),
            _trayService,
        ];
        _languageModule = new LanguageModule(_hyprland, _hyprctl, Theme.Default);
        _systemStatsModule = new SystemStatsModule(() => _snapshot.SystemStats, Theme.Default);
        _networkModule = new NetworkModule(() => _snapshot.Network, Theme.Default);
        _audioModule = new AudioModule(() => _snapshot.Audio, Theme.Default);
        _displayControlsModule =
            new DisplayControlsModule(() => _snapshot.DisplayControls, _displayControlsService, Theme.Default);
        _bluetoothModule = new BluetoothModule(() => _snapshot.Bluetooth, Theme.Default);
        _batteryModule = new BatteryModule(() => _snapshot.Battery, Theme.Default);
        _centerModule = new CenterModule(() => _notificationService.Snapshot, _notificationService, Theme.Default);
        _musicModule = new MusicModule(() => _musicService.Snapshot, Theme.Default);
        _trayModule = new TrayModule(() => _snapshot.TrayItems, _trayService, Theme.Default);
        _powerModule = new PowerModule(Theme.Default);
        _workspacesModule =
            new WorkspacesModule(_hyprland, _hyprctl, _superKey, Theme.Default, () => _languageModule.IsShown);

        _leftModules = [_workspacesModule, _musicModule];
        _rightModules =
        [
            new CompositeModule(_audioModule, _displayControlsModule, _bluetoothModule, _networkModule),
            _systemStatsModule, _languageModule, _batteryModule, _trayModule, _powerModule
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

        if (_mainDialog.IsOpen)
        {
            using var layout = new Layout(_renderer, _renderer.Width, _renderer.Height);
            layout.AddNode(_mainDialog.Draw());
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
        try
        {
            var builder = new BarStateBuilder();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            foreach (var service in _dataServices)
            {
                await service.UpdateAsync(builder, cts.Token);
            }

            _snapshot = builder.Build();
        }
        catch (Exception exception)
        {
            // Keep drawing the previous snapshot if a transient command fails.
            AppLogger.Warning("StatusBar", "Could not refresh bar state; keeping the previous snapshot", exception);
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
        _hyprctl.Dispose();
    }
}

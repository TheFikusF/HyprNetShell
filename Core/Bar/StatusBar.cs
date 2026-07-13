using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.System;
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

public sealed class StatusBar : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(700);

    private readonly HyprlandService _hyprland = new();
    private readonly SuperKeyStateService _superKey = new();
    private readonly int _barHeight;
    private readonly IRenderApi _renderer;

    private readonly NotificationService _notificationService = new();
    private readonly MusicModuleService _musicService = new();
    private readonly WorkspacesModule _workspacesModule;
    private readonly LanguageModule _languageModule;
    private readonly SystemStatsModule _systemStatsModule;
    private readonly NetworkModule _networkModule;
    private readonly AudioModule _audioModule;
    private readonly BluetoothModule _bluetoothModule;
    private readonly BatteryModule _batteryModule;
    private readonly CenterModule _centerModule;
    private readonly MusicModule _musicModule;
    private readonly TrayModule _trayModule;
    private readonly SniTrayService _trayService = new();

    private readonly List<IBarDataService> _dataServices;

    private readonly Insets _layoutInsets = new Insets(6, 6, 0, 6);

    private BarSnapshot _snapshot = BarSnapshot.Empty;
    private DateTime _lastRefresh = DateTime.MinValue;
    private Task? _refreshTask;

    public StatusBar(IRenderApi renderer, int barHeight)
    {
        _renderer = renderer;
        _barHeight = barHeight;
        _dataServices =
        [
            _notificationService,
            new NetworkModuleService(),
            new AudioModuleService(),
            new BluetoothModuleService(),
            new BatteryModuleService(),
            new SystemStatsModuleService(),
            _trayService,
        ];
        _languageModule = new LanguageModule(_hyprland, StatusBarTheme.Default);
        _systemStatsModule = new SystemStatsModule(() => _snapshot.SystemStats, StatusBarTheme.Default);
        _networkModule = new NetworkModule(() => _snapshot.Network, StatusBarTheme.Default);
        _audioModule = new AudioModule(() => _snapshot.Audio, StatusBarTheme.Default);
        _bluetoothModule = new BluetoothModule(() => _snapshot.Bluetooth, StatusBarTheme.Default);
        _batteryModule = new BatteryModule(() => _snapshot.Battery, StatusBarTheme.Default);
        _centerModule = new CenterModule(() => _snapshot.Notifications, StatusBarTheme.Default);
        _musicModule = new MusicModule(() => _musicService.Snapshot, StatusBarTheme.Default);
        _trayModule = new TrayModule(() => _snapshot.TrayItems, _trayService, StatusBarTheme.Default);
        _workspacesModule =
            new WorkspacesModule(_hyprland, _superKey, StatusBarTheme.Default, () => _languageModule.IsShown);
    }

    public void Draw()
    {
        RefreshState();

        DrawLeftRight();
        DrawCenter();
    }

    public void Dispose()
    {
        _hyprland.Dispose();
        _superKey.Dispose();
        _notificationService.Dispose();
        _musicService.Dispose();
        _trayService.Dispose();
    }

    private void DrawLeftRight()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _barHeight, new Style { Padding = _layoutInsets });
        layout.AddNode(new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 6 },
            Children =
            [
                _workspacesModule.Draw(),
                _musicModule.Draw(),
            ],
        });

        layout.AddNode(new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 6 },
            Children =
            [
                new BoxNode
                {
                    _audioModule.Draw(),
                    _bluetoothModule.Draw(),
                    _networkModule.Draw(),
                },
                _systemStatsModule.Draw(),
                _languageModule.Draw(),
                _batteryModule.Draw(),
                _trayModule.Draw(),
            ],
        });
    }

    private void DrawCenter()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _barHeight, new Style { Padding = _layoutInsets });
        layout.AddNode(_centerModule.Draw());
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
        catch
        {
            // Keep drawing the previous snapshot if a transient command fails.
        }
    }

}

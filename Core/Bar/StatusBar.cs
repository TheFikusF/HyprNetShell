using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Modules;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

public interface IDrawableModule
{
    public Node Draw();
}

file class CompositeModule(Style style, params ICollection<IDrawableModule> drawableModules) : IDrawableModule
{
    public Node Draw() => new BoxNode
    {
        Style = style,
        Children = [.. drawableModules.Select(x => x.Draw())]
    };
}

public sealed class StatusBar
{
    private readonly int _barHeight;
    private readonly IRenderApi _renderer;
    private readonly NotificationService _notificationService;
    private readonly CenterModule _centerModule;
    private readonly PopupCoordinator _popupCoordinator = new();
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
            services.Hyprctl, _popupCoordinator);
        var systemStatsModule = new SystemStatsModule(services.SystemStats, _popupCoordinator);
        var networkModule = new NetworkModule(
            services.Network,
            services.Dialogs,
            services.Tabs,
            services.ClipboardHistory, _popupCoordinator);
        var audioModule = new AudioModule(services.Audio, services.Bluetooth, _popupCoordinator);
        var privacyModule = new PrivacyModule(services.Privacy, _popupCoordinator);
        var displayControlsModule = new DisplayControlsModule(
            services.DisplayControls, _popupCoordinator);
        var bluetoothModule = new BluetoothModule(
            services.Bluetooth,
            services.Dialogs,
            services.Tabs, _popupCoordinator);
        var batteryModule = new BatteryModule(
            services.Battery,
            services.DeviceBatteries, _popupCoordinator);
        var musicModule = new MusicModule(services.Music, _popupCoordinator);
        var trayModule = new TrayModule(services.Tray, _popupCoordinator);
        var powerModule = new PowerModule(
            services.Dialogs,
            services.Hyprctl,
            services.RequestLockScreen, _popupCoordinator);
        var workspacesModule = new WorkspacesModule(
            services.Hyprland,
            services.Hyprctl,
            services.SuperKey, getOutputName,
            () => languageModule.IsShown,
            _popupCoordinator);

        var weatherWidget = new WeatherWidget(services.Weather);
        _centerModule = new CenterModule(
            services.Notifications,
            services.Calendar,
            services.ClipboardHistory,
            services.ChatGptUsage,
            services.KdeConnect,
            weatherWidget,
            services.Dialogs,
            services.Tabs, _popupCoordinator);
        _leftModules = [workspacesModule, musicModule];
        _rightModules =
        [
            privacyModule,
            new CompositeModule(new Style()
            {
                BorderRadius = 999,
                ShadowColor = Color.Black with { A = 0.45f },
                ShadowDistance = 5.0f
            }, audioModule, displayControlsModule, bluetoothModule, networkModule),
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
            Layout.DrawOnLayer(RenderLayer.Bar, _ => DrawNotificationPopups());
        }
        finally
        {
            _popupCoordinator.EndFrame();
        }
    }

    private static BoxNode DrawSide(IEnumerable<IDrawableModule> modules) => new()
    {
        Direction = Direction.Horizontal,
        VerticalAlignment = ItemsAlignment.Center,
        Style = Style.Spacer,
        Children = [.. modules.Select(x => x.Draw())],
    };

    private void DrawLeftRight()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _barHeight, new Style { Padding = _layoutInsets });
        layout.AddNode(DrawSide(_leftModules));
        layout.AddNode(DrawSide(_rightModules));
    }

    private void DrawCenter()
    {
        using var layout = new Layout(_renderer, _renderer.Width, _barHeight, new Style { Padding = _layoutInsets });
        layout.AddNode(_centerModule.Draw());
    }

    private void DrawNotificationPopups()
    {
        using var layout = new Layout(
            _renderer,
            _renderer.Width,
            _renderer.Height,
            layer: RenderLayer.BarPopup);
        layout.AddNode(new SpacerNode());
        layout.AddNode(NotificationPopupLayout.Draw(
            _notificationService.Snapshot,
            _notificationService, _renderer.Height,
            _barHeight));
    }
}

using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Rendering.Renderers;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class SystemControlsModule(
    AudioModuleService audioService,
    DisplayControlsModuleService displayService,
    AudioModule audioModule,
    DisplayControlsModule displayModule,
    BluetoothModule bluetoothModule,
    NetworkModule networkModule) : IDrawableModule
{
    private sealed class BrightnessEvents
    {
        private int _pending = -1;

        public BrightnessEvents(DisplayControlsModuleService service)
        {
            service.BrightnessChanged += value => Interlocked.Exchange(ref _pending, value);
            service.KeyboardBrightnessChanged += value => Interlocked.Exchange(ref _pending, value + KEYBOARD_EVENT_OFFSET);
        }

        public int Consume() => Interlocked.Exchange(ref _pending, -1);
    }

    private const long INDICATOR_DURATION_MS = 1500;
    private const int KEYBOARD_EVENT_OFFSET = 101;

    private readonly BrightnessEvents _brightnessEvents = new(displayService);
    private AudioDeviceSnapshot? _previousOutput;
    private AudioDeviceSnapshot? _previousInput;
    private float _visibility;
    private float _progress;
    private bool _showBrightness;
    private bool _showMicrophone;
    private bool _showKeyboard;
    private int _value;
    private bool _muted;
    private long _visibleUntil;

    public Node Draw()
    {
        UpdateIndicator();
        var group = new BoxNode {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style {
                BorderRadius = 4308,
                ShadowColor = Color.Black with { A = 0.45f },
                ShadowDistance = 5.0f,
            },
            Children = [audioModule.Draw(), displayModule.Draw(), bluetoothModule.Draw(), networkModule.Draw()],
        };

        var popupVisible = _showBrightness ? displayModule.IsPopupVisible : audioModule.IsPopupVisible;
        if (popupVisible)
        {
            _visibleUntil = 0;
        }

        var visible = Environment.TickCount64 < _visibleUntil;
        _visibility = PrimitivesMath.LerpSmooth(_visibility, visible ? 1 : 0, visible ? 20 : 12, Renderer.DeltaTime);
        _progress = PrimitivesMath.LerpSmooth(_progress, _muted ? 0 : _value / 100.0f, 18, Renderer.DeltaTime);
        if (_visibility > 0.01f)
        {
            group.Children.Add(BuildIndicator());
        }

        return group;
    }

    private void UpdateIndicator()
    {
        var audio = audioService.Snapshot;
        var output = audio.ActiveOutput;
        var input = audio.ActiveInput;
        var brightness = _brightnessEvents.Consume();
        if (output is not null && _previousOutput is not null &&
            (output.Id != _previousOutput.Id || output.Volume != _previousOutput.Volume || output.Muted != _previousOutput.Muted) &&
            !audioModule.IsPopupVisible)
        {
            _showBrightness = false;
            _showMicrophone = false;
            _value = output.Volume;
            _muted = output.Muted;
            _visibleUntil = Environment.TickCount64 + INDICATOR_DURATION_MS;
        }

        if (input is not null && _previousInput is not null &&
            (input.Id != _previousInput.Id || input.Volume != _previousInput.Volume || input.Muted != _previousInput.Muted) &&
            !audioModule.IsPopupVisible)
        {
            _showBrightness = false;
            _showMicrophone = true;
            _value = input.Volume;
            _muted = input.Muted;
            _visibleUntil = Environment.TickCount64 + INDICATOR_DURATION_MS;
        }

        if (brightness >= 0 && !displayModule.IsPopupVisible)
        {
            _showBrightness = true;
            _showKeyboard = brightness >= KEYBOARD_EVENT_OFFSET;
            _value = _showKeyboard ? brightness - KEYBOARD_EVENT_OFFSET : brightness;
            _muted = false;
            _visibleUntil = Environment.TickCount64 + INDICATOR_DURATION_MS;
        }

        _previousOutput = output;
        _previousInput = input;
    }

    private BoxNode BuildIndicator()
    {
        var theme = ThemeManager.Current;
        var icon = _showBrightness
            ? _showKeyboard ? Icons.Keyboard : Icons.Brightness[_value > 66 ? 0 : _value > 33 ? 1 : 2]
            : _showMicrophone
                ? _muted ? Icons.MicrophoneOff : Icons.Microphone
                : _muted ? Icons.VolumeMuted : Icons.VolumeLevels[_value <= 0 ? 0 : _value <= 50 ? 1 : 2];
        var scale = 0.75f + 0.25f * _visibility;
        var progressWidth = (int)MathF.Round(72 * scale);
        var progressHeight = Math.Max(1, (int)MathF.Round(6 * scale));
        var iconSize = (int)MathF.Round(24 * scale);
        var progress = new BoxNode(progressWidth, progressHeight) {
            Style = new Style { BackgroundColor = theme.Text.MutedColor, BorderRadius = 999 },
            Children =
            [
                new BoxNode((int)(progressWidth * Math.Clamp(_progress, 0, 1)), progressHeight)
                {
                    Style = new Style { BackgroundColor = Color.Orange, BorderRadius = 999 },
                },
            ],
        };

        return new BoxNode(height: (int)MathF.Round(48 * scale)) {
            Opacity = _visibility,
            IgnoreLayout = true,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style {
                BackgroundColor = theme.Panel,
                BorderRadius = 8,
                BorderWidth = theme.Border.Width,
                Padding = new Insets((int)MathF.Round(8 * scale), (int)MathF.Round(4 * scale)),
                Spacing = (int)MathF.Round(8 * scale),
            },
            Children = [new ImageNode(icon, iconSize, iconSize, theme.Text), progress,
                new TextNode($"{_value}%", theme.Text.Size * scale)],
        };
    }
}

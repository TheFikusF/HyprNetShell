using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Nodes;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class DisplayControlsModule(DisplayControlsModuleService service, Theme theme,
    ModulesCommon.PopupCoordinator popupCoordinator) : IDrawableModule
{
    private readonly ModulesCommon.NodeWithPopup _node = new(popupCoordinator, "display_controls_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    private readonly Dictionary<string, Ref<bool>> _sliderDragging = [];
    private readonly Dictionary<string, int> _overrides = [];
    private readonly Dictionary<string, ValueUpdateQueue> _updateQueues = [];
    private readonly TemperatureCurveDragState _curveDragState = new();
    private readonly Ref<float> _automaticTemperatureSwitchAnimation = new(service.IsAutomaticTemperatureEnabled() ? 1.0f : 0.0f);
    private float _iconRotation = 0;

    public Node Draw()
    {
        var controls = service.Snapshot;
        return controls.Available
            ? _node.Draw([BuildStateModule(controls)], () => BuildPopup(controls))
            : new SpacerNode();
    }

    private BoxNode BuildStateModule(DisplayControlsSnapshot controls)
    {
        var brightness = controls.Display is { } display
            ? EffectiveValue("display", display.Percentage)
            : (int?)null;

        var color = ModulesCommon.ToBackground(theme, Color.Lerp(Color.Orange, Color.White, 0.25f));
        _iconRotation = PrimitivesMath.LerpSmooth(_iconRotation, _node.IsHovered ? MathF.PI * 4 : 0, 18.0f, ModulesCommon.DELTA_TIME);
        return new BoxNode(40)
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, color, false, false) with
            {
                Spacing = 8,
                BorderWidth = new Insets(theme.BorderWidth, 0, theme.BorderWidth, 1),
                ShadowColor = null
            },
            Children = [new ImageNode(Icons.Brightness[brightness switch
            {
                > 66 => 0,
                > 33 => 1,
                _ => 2
            }], 18, 18, theme.Text)
            {
                RotationRadians = _iconRotation
            }]
        };
    }

    private BoxNode BuildPopup(DisplayControlsSnapshot controls) => new(380)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            ModulesCommon.BuildTextWithIcon(theme, Icons.Brightness[0], "Display controls"),
            BuildBacklightControl("display", "Screen brightness", Icons.Brightness[0], controls.Display),
            BuildBacklightControl("keyboard", "Keyboard brightness", Icons.Keyboard, controls.Keyboard),
            BuildTemperatureSchedule(controls),
        ],
    };

    private BoxNode BuildBacklightControl(string key, string label, SvgAsset icon, BacklightSnapshot? backlight)
    {
        if (backlight is null)
        {
            return BuildUnavailableRow(label);
        }

        var value = EffectiveValue(key, backlight.Percentage);
        return BuildSliderRow(label, icon, value / 100.0f, $"{value}%", key,
            normalized => SetValue(key, QuantizePercentage(backlight, normalized),
                percentage => service.SetBacklightAsync(backlight, percentage)));
    }

    private BoxNode BuildTemperatureSchedule(DisplayControlsSnapshot controls)
    {
        if (!controls.HyprsunsetInstalled)
        {
            return BuildUnavailableRow("Screen temperature (hyprsunset unavailable)");
        }

        var automaticTemperatureEnabled = service.IsAutomaticTemperatureEnabled();
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children =
            [
                new BoxNode
                {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = Style.Spacer,
                    Children =
                    [
                        ModulesCommon.BuildTextWithIcon(theme, Icons.Temperature, "Screen temperature"),
                        new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                        {
                            new TextNode($"{EffectiveValue("temperature", controls.TemperatureKelvin)}K", theme.TextSize, theme.Text),
                            BuildAutomaticTemperatureToggle(automaticTemperatureEnabled),
                        }
                    ],
                },
                automaticTemperatureEnabled
                    ? new TemperatureCurveNode(service.GetTemperatureCurve(), theme.Muted, Color.Orange, theme.Text, service.SetCurvePoint, _curveDragState)
                    : BuildManualTemperatureSlider(controls),
            ],
        };
    }

    private BoxNode BuildAutomaticTemperatureToggle(bool enabled) => new(44, 28)
    {
        HorizontalAlignment = ItemsAlignment.Center,
        VerticalAlignment = ItemsAlignment.Center,
        OnClick = () => service.SetAutomaticTemperatureEnabled(!enabled),
        Children =
        [
            new SwitchNode(enabled, _automaticTemperatureSwitchAnimation)
            {
                OffTrackColor = theme.Muted,
                OnTrackColor = theme.Active,
                KnobColor = theme.Text,
            },
        ],
    };

    private SliderNode BuildManualTemperatureSlider(DisplayControlsSnapshot controls)
    {
        var value = EffectiveValue("temperature", controls.TemperatureKelvin);
        return new SliderNode(340, 14,
            (value - TemperatureCurveMath.MINIMUM_TEMPERATURE) /
            (float)(TemperatureCurveMath.MAXIMUM_TEMPERATURE - TemperatureCurveMath.MINIMUM_TEMPERATURE),
            theme.Muted, Color.Orange, theme.Text,
            normalized => SetValue("temperature", TemperatureCurveMath.MINIMUM_TEMPERATURE + (int)MathF.Round(normalized *
                (TemperatureCurveMath.MAXIMUM_TEMPERATURE - TemperatureCurveMath.MINIMUM_TEMPERATURE)),
                service.SetTemperatureAsync),
            GetSliderDragging("temperature"));
    }

    private BoxNode BuildSliderRow(string label, SvgAsset icon, float normalizedValue,
        string valueText, string key, Action<float> onValueChanged) => new()
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children =
            [
                new BoxNode
                {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Children =
                    [
                        ModulesCommon.BuildTextWithIcon(theme, icon, label),
                        new TextNode(valueText, theme.TextSize, theme.Text),
                    ],
                },
                new SliderNode(340, 14, normalizedValue, theme.Muted, Color.Orange,
                    theme.Text, onValueChanged, GetSliderDragging(key)),
            ],
        };

    private BoxNode BuildUnavailableRow(string text) => new(ModulesCommon.ModuleStyle(theme, theme.Panel) with
    {
        BorderRadius = 8,
        BorderWidth = 0,
    })
    {
        new TextNode(text, theme.TextSize, theme.Muted)
    };

    private int EffectiveValue(string key, int snapshotValue)
    {
        if (_overrides.TryGetValue(key, out var value) == false)
        {
            return snapshotValue;
        }

        if (value != snapshotValue)
        {
            return value;
        }

        _overrides.Remove(key);
        return snapshotValue;
    }

    private void SetValue(string key, int value, Func<int, Task> update)
    {
        if (_overrides.GetValueOrDefault(key, int.MinValue) == value)
        {
            return;
        }

        _overrides[key] = value;
        if (_updateQueues.TryGetValue(key, out var queue) == false)
        {
            queue = new ValueUpdateQueue(update);
            _updateQueues[key] = queue;
        }

        queue.Submit(value);
    }

    private Ref<bool> GetSliderDragging(string key)
    {
        if (_sliderDragging.TryGetValue(key, out var dragging))
        {
            return dragging;
        }

        dragging = new Ref<bool>();
        _sliderDragging[key] = dragging;
        return dragging;
    }

    private static int QuantizePercentage(BacklightSnapshot backlight, float normalized)
    {
        var raw = (int)MathF.Round(Math.Clamp(normalized, 0.0f, 1.0f) * backlight.Maximum);
        return (int)MathF.Round(raw * 100.0f / backlight.Maximum);
    }

    private sealed class ValueUpdateQueue(Func<int, Task> update)
    {
        private readonly object _sync = new();

        private int _latest;
        private int _sent = int.MinValue;
        private bool _running;

        public void Submit(int value)
        {
            lock (_sync)
            {
                _latest = value;
                if (_running)
                {
                    return;
                }

                _running = true;
            }

            _ = Task.Run(ProcessAsync);
        }

        private async Task ProcessAsync()
        {
            while (true)
            {
                int value;
                lock (_sync)
                {
                    if (_sent == _latest)
                    {
                        _running = false;
                        return;
                    }

                    value = _latest;
                    _sent = value;
                }

                await update(value);
                await Task.Delay(50);
            }
        }
    }
}

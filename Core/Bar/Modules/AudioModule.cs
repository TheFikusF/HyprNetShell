using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class AudioModule(
    Func<AudioSnapshot> snapshot,
    Theme theme) : IDrawableModule
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private readonly Dictionary<string, RefBool> _sliderDragging = [];
    private readonly Dictionary<string, int> _volumeOverrides = [];
    private readonly Dictionary<string, bool> _muteOverrides = [];
    private readonly Dictionary<string, VolumeUpdateQueue> _volumeQueues = [];

    private readonly ModulesCommon.NodeWithPopup _node = new("audio_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var audio = snapshot();
        return _node.Draw([BuildStateModule(audio)], () => BuildPopup(audio));
    }

    private Node BuildStateModule(AudioSnapshot audio)
    {
        var output = audio.ActiveOutput;
        var volume = output is null ? 0 : EffectiveVolume(output);
        var icon = !audio.Available || output is null
            ? Icons.VolumeOff
            : EffectiveMuted(output)
                ? Icons.VolumeMuted
                : VolumeIcon(volume);

        var bg = ModulesCommon.ToBackground(theme, Color.Lerp(Color.Yellow, Color.Orange, 0.1f));
        return ModulesCommon.BuildTextWithIcon(theme, icon, output is not null ? $"{volume}%" : "?",
            style: ModulesCommon.ModuleStyle(theme, bg, right: false), width: 75);
    }

    private BoxNode BuildPopup(AudioSnapshot audio) => new(380)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children = !audio.Available
            ? [new TextNode("PipeWire audio unavailable", 14.0f, theme.Muted)]
            :
            [
                ..BuildDeviceSection(Icons.Speaker, "Output devices", audio.Outputs),
                ModulesCommon.BuildDivider(theme.Border),
                ..BuildDeviceSection(Icons.Microphone, "Input devices", audio.Inputs),
            ],
    };

    private IEnumerable<Node> BuildDeviceSection(
        SvgAsset icon,
        string title,
        IReadOnlyList<AudioDeviceSnapshot> devices)
    {
        yield return ModulesCommon.BuildTextWithIcon(theme, icon, title);

        if (devices.Count == 0)
        {
            yield return BuildPlainRow("No devices found");
            yield break;
        }

        foreach (var device in devices.Take(6))
        {
            yield return BuildDeviceRow(device);
        }
    }

    private Node BuildDeviceRow(AudioDeviceSnapshot device)
    {
        var state = _rowStates.GetState(device.Id, device.Active ? theme.Active : theme.Panel);
        var baseColor = device.Active ? theme.Active : theme.Panel;
        var target = state.Hovered ? Color.Lighten(baseColor, 0.12f) : baseColor;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, ModulesCommon.DELTA_TIME);
        var volume = EffectiveVolume(device);

        return new BoxNode
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Stretch,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = device.Active ? theme.BorderWidth : 0,
            },
            Children =
            [
                new BoxNode
                {
                    Style = new Style { Spacing = 8 },
                    Children =
                    [
                        new BoxNode(262, 28)
                        {
                            Direction = Direction.Horizontal,
                            VerticalAlignment = ItemsAlignment.Center,
                            Children = [new TextNode(Trim(device.Name, 32), 13.0f, theme.Text)],
                        },
                        BuildActionButton(
                            device.Active ? "●" : "○",
                            theme.Panel,
                            device.Active ? null : () => _ = AudioModuleService.SetDefaultAsync(device.Id)),
                        BuildActionButton(
                            EffectiveMuted(device) ? Icons.VolumeMuted : VolumeIcon(volume),
                            EffectiveMuted(device) ? theme.Warning : theme.Panel,
                            () => SetMuted(device, !EffectiveMuted(device))),
                    ]
                },
                new BoxNode()
                {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Padding = new Insets(16, 0, 4, 0), Spacing = 8 },
                    Children =
                    [
                        new SliderNode(
                            292,
                            12,
                            volume / 100.0f,
                            theme.Muted,
                            Color.Orange,
                            theme.Text,
                            value => SetVolume(device, (int)MathF.Round(value * 100.0f)),
                            GetSliderDragging(device.Id)),
                        new TextNode($"{volume}%", 14.0f, theme.Text),
                    ],
                }
            ],
        };
    }

    private static Node BuildActionButton(string icon, Color fill, Action? onClick) =>
        new BoxNode(30, 28)
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = onClick,
            Style = new Style
            {
                BackgroundColor = fill,
                BorderRadius = 6,
            },
            Children = [new TextNode(icon, 13.0f, Color.FromRgb(255, 255, 255))],
        };

    private static Node BuildActionButton(SvgAsset icon, Color fill, Action? onClick) =>
        new BoxNode(30, 28)
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = onClick,
            Style = new Style
            {
                BackgroundColor = fill,
                BorderRadius = 6,
            },
            Children = [new ImageNode(icon, 16, 16, Color.White)],
        };

    private static SvgAsset VolumeIcon(int volume) =>
        Icons.VolumeLevels[volume switch
        {
            <= 0 => 0,
            <= 50 => 1,
            _ => 2,
        }];

    private Node BuildPlainRow(string text) =>
        new BoxNode
        {
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
            Children = [new TextNode(text, 13.0f, theme.Muted)],
        };

    private int EffectiveVolume(AudioDeviceSnapshot device)
    {
        if (_volumeOverrides.TryGetValue(device.Id, out var volume))
        {
            if (device.Volume == volume)
            {
                _volumeOverrides.Remove(device.Id);
            }
            else
            {
                return volume;
            }
        }

        return device.Volume;
    }

    private bool EffectiveMuted(AudioDeviceSnapshot device)
    {
        if (_muteOverrides.TryGetValue(device.Id, out var muted))
        {
            if (device.Muted == muted)
            {
                _muteOverrides.Remove(device.Id);
            }
            else
            {
                return muted;
            }
        }

        return device.Muted;
    }

    private void SetMuted(AudioDeviceSnapshot device, bool muted)
    {
        _muteOverrides[device.Id] = muted;
        _ = AudioModuleService.SetMutedAsync(device.Id, muted);
    }

    private void SetVolume(AudioDeviceSnapshot device, int volume)
    {
        volume = Math.Clamp(volume, 0, 100);
        if (_volumeOverrides.GetValueOrDefault(device.Id, device.Volume) == volume)
        {
            return;
        }

        _volumeOverrides[device.Id] = volume;
        if (!_volumeQueues.TryGetValue(device.Id, out var queue))
        {
            queue = new VolumeUpdateQueue(device.Id);
            _volumeQueues[device.Id] = queue;
        }

        queue.Submit(volume);
    }

    private RefBool GetSliderDragging(string deviceId)
    {
        if (_sliderDragging.TryGetValue(deviceId, out var dragging))
        {
            return dragging;
        }

        dragging = new RefBool();
        _sliderDragging[deviceId] = dragging;
        return dragging;
    }

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";

    private sealed class VolumeUpdateQueue(string deviceId)
    {
        private readonly object _sync = new();
        private int _latest;
        private int _sent = -1;
        private bool _running;

        public void Submit(int volume)
        {
            lock (_sync)
            {
                _latest = volume;
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
                int volume;
                lock (_sync)
                {
                    if (_sent == _latest)
                    {
                        _running = false;
                        return;
                    }

                    volume = _latest;
                    _sent = volume;
                }

                await AudioModuleService.SetVolumeAsync(deviceId, volume);
                await Task.Delay(50);
            }
        }
    }
}
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class AudioModule(
    AudioModuleService service,
    BluetoothModuleService bluetoothService,
    Theme theme,
    ModulesCommon.PopupCoordinator popupCoordinator) : IDrawableModule
{
    private const int NOTE_CAPACITY = 20;
    private const long NOTE_SPAWN_INTERVAL_MS = 500;
    private const long NOTE_LIFETIME_MS = 2400;
    private const float LABEL_ANIMATION_DECAY = 18.0f;
    private const int LABEL_SPACING = 7;

    private readonly Dictionary<string, RefBool> _sliderDragging = [];
    private readonly Dictionary<string, RefFloat> _muteSwitchAnimations = [];
    private readonly Dictionary<string, int> _volumeOverrides = [];
    private readonly Dictionary<string, bool> _muteOverrides = [];
    private readonly Dictionary<string, VolumeUpdateQueue> _volumeQueues = [];
    private readonly Queue<NoteParticle> _notes = new(NOTE_CAPACITY);
    private readonly RefBool _widgetHovered = new();
    private readonly RefBool _microphoneHovered = new();
    private readonly RefBool _volumeHovered = new();

    private bool _wasWidgetHovered;
    private float _microphoneLabelWidth;
    private float _microphoneLabelSpacing;
    private float _volumeLabelWidth;
    private float _volumeLabelSpacing;
    private Color? _microphoneLabelColor;
    private Color? _volumeLabelColor;
    private float _noteFieldOpacity;
    private long _nextNoteSpawnMs;
    private long _noteSequence;

    private readonly ModulesCommon.NodeWithPopup _node = new(popupCoordinator, "audio_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var audio = service.Snapshot;
        return _node.Draw([BuildStateModule(audio)], () => BuildPopup(audio));
    }

    private Node BuildStateModule(AudioSnapshot audio)
    {
        var output = audio.ActiveOutput;
        var input = audio.ActiveInput;
        var volume = output is null ? 0 : EffectiveVolume(output);
        var inputMuted = input is not null && EffectiveMuted(input);
        var volumeIcon = !audio.Available || output is null
            ? Icons.VolumeOff
            : EffectiveMuted(output)
                ? Icons.VolumeMuted
                : VolumeIcon(volume);
        var microphoneIcon = !audio.Available || input is null || inputMuted
            ? Icons.MicrophoneOff
            : Icons.Microphone;
        var microphoneColor = !audio.Available || input is null
            ? theme.Muted
            : theme.Text;

        var bg = ModulesCommon.ToBackground(theme, Color.Lerp(Color.Yellow, Color.Orange, 0.1f));
        var microphoneControl = BuildHoverLabel(
            BuildMicrophoneIcon(microphoneIcon, microphoneColor, audio.IsRecording),
            input is not null ? $"{EffectiveVolume(input)}%" : "?",
            _microphoneHovered,
            ref _microphoneLabelWidth,
            ref _microphoneLabelSpacing,
            ref _microphoneLabelColor,
            input is null ? null : () => SetMuted(input, !EffectiveMuted(input)),
            input is null ? null : delta => AdjustVolume(input, delta));

        var volumeControl = BuildHoverLabel(
            new ImageNode(volumeIcon, 18, 18, theme.Text),
            output is not null ? $"{volume}%" : "?",
            _volumeHovered,
            ref _volumeLabelWidth,
            ref _volumeLabelSpacing,
            ref _volumeLabelColor,
            output is null ? null : () => SetMuted(output, !EffectiveMuted(output)),
            output is null ? null : delta => AdjustVolume(output, delta));

        var widget = new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            IsHovered = _widgetHovered,
            Style = ModulesCommon.ModuleStyle(theme, bg, right: false) with
            {
                Spacing = 0,
                Padding = new Insets(4, 0)
            },
            Children =
            [
                microphoneControl,
                volumeControl,
            ],
        };

        foreach (var particle in BuildNoteParticles(widget.Width))
        {
            widget.Children.Add(particle);
        }

        return widget;
    }

    private BoxNode BuildHoverLabel(
        Node icon,
        string text,
        RefBool hovered,
        ref float animatedWidth,
        ref float animatedSpacing,
        ref Color? animatedColor,
        Action? onClick,
        Action<float>? onScroll)
    {
        var label = new TextNode(text, theme.TextSize, theme.Text);
        var targetWidth = hovered.Value ? label.Width : 0.0f;
        var targetSpacing = hovered.Value ? LABEL_SPACING : 0.0f;
        var hiddenColor = theme.Text with { A = 0.0f };
        var targetColor = hovered.Value ? theme.Text : hiddenColor;

        animatedWidth = PrimitivesMath.LerpSmooth(
            animatedWidth,
            targetWidth,
            LABEL_ANIMATION_DECAY,
            ModulesCommon.DELTA_TIME);
        animatedSpacing = PrimitivesMath.LerpSmooth(
            animatedSpacing,
            targetSpacing,
            LABEL_ANIMATION_DECAY,
            ModulesCommon.DELTA_TIME);
        animatedColor = (animatedColor ?? hiddenColor).LerpSmooth(
            targetColor,
            LABEL_ANIMATION_DECAY,
            ModulesCommon.DELTA_TIME);

        if (MathF.Abs(animatedWidth - targetWidth) < 0.05f)
        {
            animatedWidth = targetWidth;
        }

        if (MathF.Abs(animatedSpacing - targetSpacing) < 0.05f)
        {
            animatedSpacing = targetSpacing;
        }

        var visibleWidth = (int)MathF.Ceiling(animatedWidth);
        var children = new List<Node> { icon };
        if (visibleWidth > 0)
        {
            children.Add(new BoxNode(visibleWidth)
            {
                VerticalAlignment = ItemsAlignment.Center,
                Children =
                [
                    new TextNode(text, theme.TextSize, animatedColor.Value, maxWidth: visibleWidth),
                ],
            });
        }

        return new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = hovered,
            OnClick = onClick,
            OnScroll = onScroll,
            Style = new Style { Spacing = (int)MathF.Round(animatedSpacing), Padding = new Insets(4, 6) },
            Children = children,
        };
    }

    private Node BuildMicrophoneIcon(SvgAsset icon, Color color, bool isRecording) => new BoxNode(18, 18)
    {
        Children =
        [
            new ImageNode(icon, 18, 18, color),
            isRecording
                ? new BoxNode(7, 7)
                {
                    IgnoreLayout = true,
                    Right = -2,
                    Bottom = -2,
                    Opacity = RecordingIndicatorOpacity(),
                    Style = new Style
                    {
                        BackgroundColor = Color.FromRgb(245, 45, 55),
                        BorderColor = theme.Panel,
                        BorderRadius = 3.5f,
                        BorderWidth = 1,
                    },
                }
                : new SpacerNode(),
        ],
    };

    private static float RecordingIndicatorOpacity()
    {
        const double PERIOD_MS = 1800.0;
        var phase = Environment.TickCount64 % PERIOD_MS / PERIOD_MS * Math.PI * 2.0;
        return 0.58f + 0.42f * (float)((Math.Sin(phase) + 1.0) * 0.5);
    }

    private IReadOnlyList<Node> BuildNoteParticles(int widgetWidth)
    {
        var now = Environment.TickCount64;
        var hovered = _widgetHovered.Value;

        while (_notes.TryPeek(out var note) && now - note.SpawnedAtMs >= NOTE_LIFETIME_MS)
        {
            _notes.Dequeue();
        }

        if (hovered && !_wasWidgetHovered)
        {
            _nextNoteSpawnMs = now;
        }

        if (hovered)
        {
            var spawned = 0;
            while (now >= _nextNoteSpawnMs && spawned < NOTE_CAPACITY)
            {
                if (_notes.Count == NOTE_CAPACITY)
                {
                    _notes.Dequeue();
                }

                _notes.Enqueue(new NoteParticle(_nextNoteSpawnMs, _noteSequence++));
                _nextNoteSpawnMs += NOTE_SPAWN_INTERVAL_MS;
                spawned++;
            }

            if (now - _nextNoteSpawnMs > NOTE_SPAWN_INTERVAL_MS * NOTE_CAPACITY)
            {
                _nextNoteSpawnMs = now + NOTE_SPAWN_INTERVAL_MS;
            }
        }

        _wasWidgetHovered = hovered;
        _noteFieldOpacity = PrimitivesMath.LerpSmooth(
            _noteFieldOpacity,
            hovered ? 1.0f : 0.0f,
            14.0f,
            ModulesCommon.DELTA_TIME);

        if (_noteFieldOpacity < 0.01f || _notes.Count == 0)
        {
            return [];
        }

        var color = Color.Lerp(theme.Text, Color.Orange, 0.55f);
        return _notes.Select(note =>
        {
            var progress = Math.Clamp((now - note.SpawnedAtMs) / (float)NOTE_LIFETIME_MS, 0.0f, 1.0f);
            var fadeIn = Math.Min(1.0f, progress / 0.12f);
            var fadeOut = Math.Min(1.0f, (1.0f - progress) / 0.25f);
            var opacity = _noteFieldOpacity * Math.Min(fadeIn, fadeOut) * 0.62f;
            var size = note.Sequence % 3 == 0 ? 10 : 8;
            var availableWidth = Math.Max(1, widgetWidth - size - 8);
            var left = 4 + (int)(note.Sequence * 29 % availableWidth);
            var top = 18 - (int)MathF.Round(progress * 20.0f);

            return (Node)new BoxNode(size, size)
            {
                IgnoreLayout = true,
                Left = left,
                Top = top,
                Opacity = opacity,
                Children =
                [
                    new ImageNode(Icons.MusicNotes[note.Sequence % Icons.MusicNotes.Length], size, size, color),
                ],
            };
        }).ToArray();
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
                ..BuildDeviceSection(Icons.Speaker, "Output devices", audio.Outputs, false),
                ModulesCommon.BuildDivider(theme.Border),
                ..BuildDeviceSection(Icons.Microphone, "Input devices", audio.Inputs, true),
            ],
    };

    private IEnumerable<Node> BuildDeviceSection(
        SvgAsset icon,
        string title,
        IReadOnlyList<AudioDeviceSnapshot> devices,
        bool input)
    {
        yield return ModulesCommon.BuildTextWithIcon(theme, icon, title);

        if (devices.Count == 0)
        {
            yield return BuildPlainRow("No devices found");
            yield break;
        }

        foreach (var device in devices.Take(6))
        {
            yield return BuildDeviceRow(device, input);
        }
    }

    private BoxNode BuildDeviceRow(AudioDeviceSnapshot device, bool input)
    {
        var volume = EffectiveVolume(device);
        var muted = EffectiveMuted(device);
        var bluetoothDevice = FindBluetoothDevice(device.Name);

        return new BoxNode
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 0,
            },
            Children =
            [
                new BoxNode(new Style { Spacing = 8 },
                    horizontalAlignment: ItemsAlignment.Stretch,
                    verticalAlignment: ItemsAlignment.Center)
                {
                    new BoxNode(24)
                    {
                        HorizontalAlignment = ItemsAlignment.Center,
                        VerticalAlignment = ItemsAlignment.Center,
                        OnClick = device.Active ? null : () => _ = service.SetDefaultAsync(device.Id),
                        Children =
                        [
                            new RadioButtonNode(device.Active)
                            {
                                SelectedColor = Color.Orange,
                                UnselectedColor = theme.Muted,
                                BackgroundColor = theme.Panel,
                            }
                        ],
                    },
                    new ImageNode(DeviceIcon(device.Name, input, bluetoothDevice), 18, 18,
                        muted ? theme.Muted : theme.Text),
                    new BoxNode
                    {
                        Direction = Direction.Horizontal,
                        VerticalAlignment = ItemsAlignment.Center,
                        Children = [new TextNode(Trim(device.Name, 30), 13.0f, theme.Text)],
                    },
                    new BoxNode(44)
                    {
                        OnClick = () => SetMuted(device, !EffectiveMuted(device)),
                        Children =
                        [
                            new SwitchNode(muted, GetMuteSwitchAnimation(device.Id, muted))
                            {
                                OffTrackColor = theme.Muted,
                                OnTrackColor = theme.Warning,
                                KnobColor = theme.Text,
                            }
                        ],
                    },
                    new ImageNode(input ? Icons.MicrophoneOff : Icons.VolumeMuted, 18, 18,
                        muted ? theme.Warning : theme.Muted),
                },
                ..BuildBluetoothBattery(bluetoothDevice),
                new BoxNode
                {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Padding = new Insets(16, 0, 4, 0), Spacing = 8 },
                    Children =
                    [
                        new SliderNode(
                            292,
                            14,
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

    private IEnumerable<Node> BuildBluetoothBattery(BluetoothDeviceSnapshot? device)
    {
        if (device?.BatteryPercentage is not { } battery)
        {
            yield break;
        }

        yield return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Padding = new Insets(8, 0, 0, 0) },
            Children =
            [
                new TextNode("Battery", theme.TextSize, theme.Text),
                ModulesCommon.BuildTextWithIcon(
                    theme,
                    BatteryModule.BatteryLevelIcon(battery),
                    $"{battery}%",
                    battery <= 20 ? Color.Lerp(Color.White, Color.Orange, 0.5f) : theme.Text),
            ],
        };
    }

    private static SvgAsset VolumeIcon(int volume) => Icons.VolumeLevels[volume switch
    {
        <= 0 => 0,
        <= 50 => 1,
        _ => 2,
    }];

    private BoxNode BuildPlainRow(string text) => new ()
    {
        Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
        Children = [new TextNode(text, theme.TextSize, theme.Muted)],
    };

    private int EffectiveVolume(AudioDeviceSnapshot device)
    {
        if (_volumeOverrides.TryGetValue(device.Id, out var volume) == false)
        {
            return device.Volume;
        }

        if (device.Volume != volume)
        {
            return volume;
        }

        _volumeOverrides.Remove(device.Id);
        return device.Volume;
    }

    private bool EffectiveMuted(AudioDeviceSnapshot device)
    {
        if (_muteOverrides.TryGetValue(device.Id, out var muted) == false)
        {
            return device.Muted;
        }

        if (device.Muted != muted)
        {
            return muted;
        }

        _muteOverrides.Remove(device.Id);
        return device.Muted;
    }

    private void SetMuted(AudioDeviceSnapshot device, bool muted)
    {
        _muteOverrides[device.Id] = muted;
        _ = service.SetMutedAsync(device.Id, muted);
    }

    private void AdjustVolume(AudioDeviceSnapshot device, float scrollDelta)
    {
        const int SCROLL_STEP = 5;
        var direction = scrollDelta < 0.0f ? 1 : -1;
        SetVolume(device, EffectiveVolume(device) + direction * SCROLL_STEP);
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
            queue = new VolumeUpdateQueue(service, device.Id);
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

    private RefFloat GetMuteSwitchAnimation(string deviceId, bool muted)
    {
        if (_muteSwitchAnimations.TryGetValue(deviceId, out var animation))
        {
            return animation;
        }

        animation = new RefFloat(muted ? 1.0f : 0.0f);
        _muteSwitchAnimations[deviceId] = animation;
        return animation;
    }

    private BluetoothDeviceSnapshot? FindBluetoothDevice(string audioDeviceName)
    {
        var normalizedAudioName = NormalizeDeviceName(audioDeviceName);
        return bluetoothService.Snapshot.Devices.FirstOrDefault(device =>
        {
            var normalizedBluetoothName = NormalizeDeviceName(device.Name);
            return normalizedAudioName == normalizedBluetoothName ||
                   Math.Min(normalizedAudioName.Length, normalizedBluetoothName.Length) >= 6 &&
                   (normalizedAudioName.Contains(normalizedBluetoothName, StringComparison.Ordinal) ||
                    normalizedBluetoothName.Contains(normalizedAudioName, StringComparison.Ordinal));
        });
    }

    private static string NormalizeDeviceName(string name) =>
        new(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static SvgAsset DeviceIcon(
        string name,
        bool input,
        BluetoothDeviceSnapshot? bluetoothDevice)
    {
        if (input)
        {
            return name.Contains("headset", StringComparison.OrdinalIgnoreCase) ||
                   bluetoothDevice?.Icon?.Equals("audio-headset", StringComparison.OrdinalIgnoreCase) == true
                ? Icons.Headset
                : Icons.Microphone;
        }

        if (name.Contains("headset", StringComparison.OrdinalIgnoreCase))
        {
            return Icons.Headset;
        }

        if (name.Contains("headphone", StringComparison.OrdinalIgnoreCase))
        {
            return Icons.Headphones;
        }

        if (bluetoothDevice?.Icon is { } bluetoothIcon)
        {
            return bluetoothIcon.ToLowerInvariant() switch
            {
                "audio-headset" => Icons.Headset,
                "audio-speakers" => Icons.Speaker,
                "audio-headphones" or "audio-card" => Icons.Headphones,
                _ => Icons.Headphones,
            };
        }

        if (bluetoothDevice is not null)
        {
            return Icons.Headphones;
        }

        if (name.Contains("hdmi", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("display", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("monitor", StringComparison.OrdinalIgnoreCase))
        {
            return Icons.Monitor;
        }

        return Icons.Speaker;
    }

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";

    private readonly record struct NoteParticle(long SpawnedAtMs, long Sequence);

    private sealed class VolumeUpdateQueue(AudioModuleService service, string deviceId)
    {
        private readonly Lock _sync = new();
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

                await service.SetVolumeAsync(deviceId, volume);
                await Task.Delay(50);
            }
        }
    }
}
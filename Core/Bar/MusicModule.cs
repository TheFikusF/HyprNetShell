using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using System.Globalization;

namespace HyprNetShell.Core.Bar;

internal sealed class MusicModule(
    Func<MusicSnapshot> snapshot,
    StatusBarTheme theme) : IDrawableModule
{
    private enum PlayerAction
    {
        PlayPause,
        Previous,
        Next,
    }

    private const int VISIBLE_CHARACTERS = 40;
    private const int IMAGE_SIZE = 34;
    private const int POPUP_WIDTH = 512;
    private const int POPUP_IMAGE_SIZE = 128;
    private readonly ModulesCommon.NodeWithPopup _node = new()
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };
    private readonly Dictionary<PlayerAction, ModulesCommon.BoxState> _buttonStates = [];
    private readonly RefBool _progressDragging = new();
    private readonly SeekUpdateQueue _seekQueue = new();
    private long _positionOverrideMicros;

    public Node Draw()
    {
        var module = snapshot();
        if (!module.Available)
        {
            return new SpacerNode();
        }

        return _node.Draw([
            BuildCover(module),
            BuildTextBody(module)
        ], () => BuildPopup(module));
    }

    private Node BuildCover(MusicSnapshot music) =>
        BuildSurface(music, IMAGE_SIZE + 6, IMAGE_SIZE + 6, 4, theme.BorderWidth,
        [
            string.IsNullOrWhiteSpace(music.ImagePath)
                ? ModulesCommon.BuildBadge("M", 14.0f, theme.Muted, theme)
                : new ImageNode(music.ImagePath, IMAGE_SIZE, IMAGE_SIZE),
        ], () => Control(music, PlayerAction.PlayPause));

    private Node BuildTextBody(MusicSnapshot music) =>
        BuildSurface(music, null, null, new BorderRadius(0, theme.Radius, theme.Radius, 0),
            new Insets(theme.BorderWidth, theme.BorderWidth, theme.BorderWidth, 0),
            [
                new MarqueeTextNode(music.Label, VISIBLE_CHARACTERS, 14.0f, theme.Text),
            ]);

    private Node BuildSurface(
        MusicSnapshot music,
        int? width,
        int? height,
        BorderRadius radius,
        Insets borderWidth,
        List<Node> children,
        Action? onClick = null)
    {
        var style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
        {
            BorderColor = theme.Border,
            BorderRadius = radius,
            BorderWidth = borderWidth,
            Padding = width != null ? 0 : new Insets(8, 6),
            Spacing = 8
        };

        return music.Playing
            ? new GradientBoxNode(Color.FromRgb(255, 214, 66), Color.FromRgb(255, 121, 24), GradientOffset, width,
                height)
            {
                Direction = Direction.Horizontal,
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                OnClick = onClick,
                Style = style,
                Children = children
            }
            : new BoxNode(width, height)
            {
                Direction = Direction.Horizontal,
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                OnClick = onClick,
                Style = style,
                Children = children
            };
    }

    private Node BuildPopup(MusicSnapshot music) =>
        new BoxNode(POPUP_WIDTH)
        {
            IgnoreLayout = true,
            Style = new Style
            {
                Padding = new Insets(32, 0, 0, 0),
            },
            Children =
            [
                new BoxNode(POPUP_WIDTH)
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Start,
                    Style = ModulesCommon.ModuleStyle(theme, Color.FromRgb(0, 0, 0, 0.93f)) with
                    {
                        BorderRadius = 8,
                        BorderWidth = 2,
                        Padding = 12,
                        Spacing = 10
                    },
                    Children =
                    [
                        new BoxNode
                        {
                            Direction = Direction.Horizontal,
                            VerticalAlignment = ItemsAlignment.Start,
                            Style = new Style { Spacing = 12 },
                            Children =
                            [
                                BuildPopupImage(music),
                                new BoxNode(POPUP_WIDTH - POPUP_IMAGE_SIZE - 42)
                                {
                                    Direction = Direction.Vertical,
                                    VerticalAlignment = ItemsAlignment.Start,
                                    Style = new Style { Spacing = 5 },
                                    Children =
                                    [
                                        new MarqueeTextNode(music.Title, 45, 16.0f, theme.Text),
                                        new MarqueeTextNode(FormatSubtitle(music), 47, 14.0f, theme.Text),
                                        new MarqueeTextNode(music.Player, 47, 14.0f, theme.Text),
                                    ]
                                }
                            ]
                        },
                        BuildProgress(music),
                        new BoxNode(POPUP_WIDTH - 30)
                        {
                            Direction = Direction.Horizontal,
                            HorizontalAlignment = ItemsAlignment.Center,
                            VerticalAlignment = ItemsAlignment.Center,
                            Style = new Style { Spacing = 12 },
                            Children =
                            [
                                BuildControlButton(PlayerAction.Previous, music),
                                BuildControlButton(PlayerAction.PlayPause, music),
                                BuildControlButton(PlayerAction.Next, music),
                            ]
                        }
                    ]
                }
            ]
        };

    private Node BuildPopupImage(MusicSnapshot music) =>
        string.IsNullOrWhiteSpace(music.ImagePath)
            ? new BoxNode(POPUP_IMAGE_SIZE, POPUP_IMAGE_SIZE)
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
                Children = [new TextNode("M", 28.0f, theme.Muted)]
            }
            : new ImageNode(music.ImagePath, POPUP_IMAGE_SIZE, POPUP_IMAGE_SIZE);

    private Node BuildProgress(MusicSnapshot music)
    {
        var width = POPUP_WIDTH - 30;
        var position = _progressDragging.Value ? _positionOverrideMicros : EffectivePosition(music);
        var ratio = music.LengthMicros > 0
            ? Math.Clamp((float)position / music.LengthMicros, 0.0f, 1.0f)
            : 0.0f;

        return new BoxNode(width)
        {
            Direction = Direction.Vertical,
            Style = new Style { Spacing = 5 },
            Children =
            [
                new SliderNode(
                    width,
                    18,
                    ratio,
                    theme.Panel,
                    music.Playing ? theme.Active : theme.Muted,
                    theme.Text,
                    value => Seek(music, value),
                    _progressDragging),
                new BoxNode(width)
                {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Children =
                    [
                        new TextNode(FormatTime(position), 14.0f, theme.Text),
                        new TextNode(FormatTime(music.LengthMicros), 14.0f, theme.Text),
                    ]
                }
            ]
        };
    }

    private static long EffectivePosition(MusicSnapshot music)
    {
        if (!music.Playing || music.LengthMicros <= 0)
        {
            return music.PositionMicros;
        }

        var elapsedMicros = Math.Max(0, (DateTime.UtcNow - music.PositionObservedAtUtc).Ticks / 10);
        return Math.Min(music.LengthMicros, music.PositionMicros + elapsedMicros);
    }

    private void Seek(MusicSnapshot music, float ratio)
    {
        if (music.LengthMicros <= 0)
        {
            return;
        }

        _positionOverrideMicros = (long)(music.LengthMicros * Math.Clamp(ratio, 0.0f, 1.0f));
        _seekQueue.Submit(music.Bus, music.Player, music.PositionMicros, _positionOverrideMicros);
    }

    private Node BuildControlButton(PlayerAction action, MusicSnapshot music)
    {
        var size = action == PlayerAction.PlayPause ? 44 : 36;
        var defaultColor = action == PlayerAction.PlayPause ? theme.Active : theme.Panel;
        var state = GetButtonState(action);
        var target = state.Hovered
            ? Color.Lighten(defaultColor, 0.16f)
            : defaultColor;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, ModulesCommon.DELTA_TIME);

        return new BoxNode(size, size)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => Control(music, action),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 999,
                Padding = new Insets(8, 8)
            },
            Children =
            [
                new TextNode(action switch
                {
                    PlayerAction.PlayPause => music.Playing ? "[]" : ">",
                    PlayerAction.Previous => "[<",
                    PlayerAction.Next => ">]",
                    _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
                }, 14.0f, theme.Text)
            ]
        };
    }

    private ModulesCommon.BoxState GetButtonState(PlayerAction action)
    {
        if (_buttonStates.TryGetValue(action, out var state))
        {
            return state;
        }

        state = new ModulesCommon.BoxState(theme.Panel);
        _buttonStates[action] = state;
        return state;
    }

    private static float GradientOffset() => -(float)(Environment.TickCount64 % 2600 / 2600.0);

    private static string FormatSubtitle(MusicSnapshot music) =>
        (music.Artist, music.Album) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{music.Artist} - {music.Album}",
            ({ Length: > 0 }, _) => music.Artist,
            (_, { Length: > 0 }) => music.Album,
            _ => "",
        };

    private static string FormatTime(long micros)
    {
        if (micros <= 0)
        {
            return "0:00";
        }

        var totalSeconds = micros / 1_000_000;
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes}:{seconds:00}";
    }

    private static void Control(MusicSnapshot music, PlayerAction action)
    {
        _ = Task.Run(async () =>
        {
            if (!string.IsNullOrWhiteSpace(music.Bus))
            {
                await CommandRunner.TryReadAsync(
                    "gdbus",
                    $"call --session --dest {music.Bus} --object-path /org/mpris/MediaPlayer2 --method org.mpris.MediaPlayer2.Player.{action}",
                    TimeSpan.FromMilliseconds(500),
                    CancellationToken.None);
                return;
            }

            await CommandRunner.TryReadAsync(
                "playerctl",
                action switch
                {
                    PlayerAction.PlayPause => "play-pause",
                    PlayerAction.Previous => "previous",
                    PlayerAction.Next => "next",
                    _ => "play-pause",
                },
                TimeSpan.FromMilliseconds(500),
                CancellationToken.None);
        });
    }

    private sealed class SeekUpdateQueue
    {
        private readonly object _sync = new();
        private string _bus = "";
        private string _player = "";
        private long _latestMicros;
        private long _sentMicros = -1;
        private bool _running;

        public void Submit(string bus, string player, long currentMicros, long positionMicros)
        {
            lock (_sync)
            {
                if (!_running ||
                    !string.Equals(_bus, bus, StringComparison.Ordinal) ||
                    !string.Equals(_player, player, StringComparison.Ordinal))
                {
                    _sentMicros = currentMicros;
                }

                _bus = bus;
                _player = player;
                _latestMicros = positionMicros;
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
                string bus;
                string player;
                long positionMicros;
                long previousMicros;
                lock (_sync)
                {
                    if (_sentMicros == _latestMicros)
                    {
                        _running = false;
                        return;
                    }

                    bus = _bus;
                    player = _player;
                    positionMicros = _latestMicros;
                    previousMicros = _sentMicros;
                    _sentMicros = positionMicros;
                }

                if (!string.IsNullOrWhiteSpace(bus))
                {
                    await CommandRunner.TryRunAsync(
                        "gdbus",
                        [
                            "call",
                            "--session",
                            "--dest", bus,
                            "--object-path", "/org/mpris/MediaPlayer2",
                            "--method", "org.mpris.MediaPlayer2.Player.Seek",
                            (positionMicros - previousMicros).ToString(CultureInfo.InvariantCulture),
                        ],
                        TimeSpan.FromMilliseconds(700),
                        CancellationToken.None);
                    await Task.Delay(50);
                    continue;
                }

                var arguments = new List<string>();
                if (!string.IsNullOrWhiteSpace(player))
                {
                    arguments.AddRange(["--player", player]);
                }

                arguments.AddRange([
                    "position",
                    (positionMicros / 1_000_000.0).ToString("0.######", CultureInfo.InvariantCulture),
                ]);
                await CommandRunner.TryRunAsync(
                    "playerctl",
                    arguments,
                    TimeSpan.FromMilliseconds(700),
                    CancellationToken.None);
                await Task.Delay(50);
            }
        }
    }
}

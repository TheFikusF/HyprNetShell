using System.Globalization;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class MusicModule(
    MusicModuleService service,
    Theme theme,
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private enum PlayerAction
    {
        PlayPause,
        Previous,
        Next,
        Shuffle,
        Repeat,
    }

    private const int VISIBLE_CHARACTERS = 35;
    private const int IMAGE_SIZE = 34;
    private const int POPUP_WIDTH = 512 + 64;
    private const int POPUP_IMAGE_SIZE = 128 + 64;
    private const int QUEUE_IMAGE_SIZE = 42;

    private readonly NodeWithPopup _node = new(popupCoordinator, "music_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    private readonly Dictionary<PlayerAction, ModulesCommon.BoxState> _buttonStates = [];
    private readonly Dictionary<(string Uri, int Position), ModulesCommon.BoxState> _queueCoverStates = [];
    private readonly Dictionary<(string Uri, int Position), ModulesCommon.BoxState> _queueRemoveStates = [];
    private readonly Ref<bool> _progressDragging = new();
    private readonly ModulesCommon.BoxState _coverButton = new() { Background = Color.White with { A = 0 } };
    private readonly SeekUpdateQueue _seekQueue = new();
    private long _positionOverrideMicros;

    public Node Draw()
    {
        var music = service.Snapshot;
        if (!music.Available)
        {
            return new SpacerNode();
        }

        return _node.Draw([

            BuildTextBody(music)
        ], () => BuildPopup(music));
    }

    private Node BuildCover(MusicSnapshot music)
    {
        _coverButton.Background = Color.LerpSmooth(_coverButton.Background,
            _coverButton.Hovered || string.IsNullOrWhiteSpace(music.ImagePath)
                ? Color.White
                : Color.White with { A = 0 }, 18.0f, Renderer.DeltaTime);

        return BuildSurface(music,
            [
                string.IsNullOrWhiteSpace(music.ImagePath)
                    ? new SpacerNode()
                    : new ImageNode(music.ImagePath, IMAGE_SIZE, IMAGE_SIZE),
                new BoxNode(IMAGE_SIZE, IMAGE_SIZE)
                {
                    IgnoreLayout = true,
                    VerticalAlignment = ItemsAlignment.Center,
                    HorizontalAlignment = ItemsAlignment.Center,
                    IsHovered = _coverButton.Hovered,
                    Style = new Style
                    {
                        BackgroundColor = Color.Black with
                        {
                            A = _coverButton.Background.A * (string.IsNullOrWhiteSpace(music.ImagePath) ? 0 : 0.6f)
                        }
                    },
                    Children = [new ImageNode(music.Playing ? Icons.Pause : Icons.Play, 18, 18, _coverButton.Background)]
                }
            ],
            width: IMAGE_SIZE + 6, height: IMAGE_SIZE + 6, 4, 0,
            onClick: () => Control(music, PlayerAction.PlayPause),
            ignoreLayout: true);
    }

    private Node BuildTextBody(MusicSnapshot music) => BuildSurface(music,
        [
            new BoxNode
            {
                IgnoreLayout = true,
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Center,
                Left = 0,
                Children =
                [
                    BuildSurface(music, [new ImageNode(Icons.SkipBack, 18, 18, theme.Text)],
                        right: false,
                        onClick: () => Control(music, PlayerAction.Previous),
                        padding: new Insets(6, 4)
                    ),
                    new BoxNode(IMAGE_SIZE),
                    BuildCover(music),
                    BuildSurface(music, [new ImageNode(Icons.SkipForward, 18, 18, theme.Text)],
                        left: false,
                        onClick: () => Control(music, PlayerAction.Next),
                        padding: new Insets(6, 4)
                    )
                ]
            },
            new BoxNode(18 * 2 + 6 * 4 + 2 * 2 + IMAGE_SIZE),
            new MarqueeTextNode(music.Label, VISIBLE_CHARACTERS, 14.0f, theme.Text)
        ], padding: new Insets(6, 8, 6, -2),
        horizontalAlignment: ItemsAlignment.Start, darken: 0.5f
    );

    private Node BuildSurface(
        MusicSnapshot music,
        ICollection<Node> children,
        int? width = null,
        int? height = null,
        BorderRadius? radius = null,
        Insets? padding = null,
        bool left = true,
        bool right = true,
        Action? onClick = null,
        bool ignoreLayout = false,
        bool shadow = true,
        ItemsAlignment horizontalAlignment = ItemsAlignment.Center,
        ItemsAlignment verticalAlignment = ItemsAlignment.Center,
        float darken = 0)
    {
        var style = ModulesCommon.ModuleStyle(theme, theme.Panel, left, right) with { Spacing = 8 };

        if (radius.HasValue)
        {
            style = style with { BorderRadius = radius.Value };
        }

        if (padding.HasValue)
        {
            style = style with { Padding = padding.Value };
        }

        if (shadow == false)
        {
            style = style with { ShadowColor = null };
        }

        return music.Playing
            ? new GradientBoxNode(Color.Darken(Color.FromRgb(255, 214, 66), darken),
                Color.Darken(Color.FromRgb(255, 121, 24), darken),
                GradientOffset, width, height)
            {
                IgnoreLayout = ignoreLayout,
                Direction = Direction.Horizontal,
                HorizontalAlignment = horizontalAlignment,
                VerticalAlignment = verticalAlignment,
                OnClick = onClick,
                Style = style,
                Children = children
            }
            : new BoxNode(width, height)
            {
                IgnoreLayout = ignoreLayout,
                Direction = Direction.Horizontal,
                HorizontalAlignment = horizontalAlignment,
                VerticalAlignment = verticalAlignment,
                OnClick = onClick,
                Style = style,
                Children = children
            };
    }

    private BoxNode BuildPopup(MusicSnapshot music) => new(POPUP_WIDTH)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children = music.IsSpotify
            ? [BuildNowPlaying(music), BuildSpotifyQueue(music)]
            : [BuildNowPlaying(music)]
    };

    private BoxNode BuildNowPlaying(MusicSnapshot music) => new(height: POPUP_IMAGE_SIZE)
    {
        Direction = Direction.Horizontal,
        VerticalAlignment = ItemsAlignment.Start,
        Style = Style.Spacer,
        Children =
        [
            BuildPopupImage(music),
            new BoxNode(POPUP_WIDTH - POPUP_IMAGE_SIZE - 42, POPUP_IMAGE_SIZE)
            {
                Direction = Direction.Vertical,
                VerticalAlignment = ItemsAlignment.Spread,
                Style = new Style { Padding = new Insets(0, 12) },
                Children =
                [
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        Style = new Style { Spacing = 6 },
                        Children =
                        [
                            new MarqueeTextNode(music.Title, 42, 16.0f, theme.Text),
                            new MarqueeTextNode(FormatSubtitle(music), 47, theme.Text, theme.Text),
                            new MarqueeTextNode(music.Player, 47, theme.Text, theme.Text.MutedColor),
                        ]
                    },
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        Style = Style.Spacer,
                        Children =
                        [
                            BuildProgress(music),
                            new BoxNode(POPUP_WIDTH - POPUP_IMAGE_SIZE - 42)
                            {
                                Direction = Direction.Horizontal,
                                HorizontalAlignment = ItemsAlignment.Center,
                                VerticalAlignment = ItemsAlignment.Center,
                                Style = new Style { Spacing = music.IsSpotify ? 8 : 12 },
                                Children = music.IsSpotify
                                    ?
                                    [
                                        BuildControlButton(PlayerAction.Shuffle, music),
                                        BuildControlButton(PlayerAction.Previous, music),
                                        BuildControlButton(PlayerAction.PlayPause, music),
                                        BuildControlButton(PlayerAction.Next, music),
                                        BuildControlButton(PlayerAction.Repeat, music),
                                    ]
                                    :
                                    [
                                        BuildControlButton(PlayerAction.Previous, music),
                                        BuildControlButton(PlayerAction.PlayPause, music),
                                        BuildControlButton(PlayerAction.Next, music),
                                    ]
                            }
                        ]
                    },
                ]
            }
        ]
    };

    private BoxNode BuildSpotifyQueue(MusicSnapshot music) => new()
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children =
        [
            ModulesCommon.BuildDivider(theme.Text, height: 0),
            new TextNode("QUEUE", theme.Text, theme.Text),
            ..BuildQueueRows(music)
        ]
    };

    private IEnumerable<Node> BuildQueueRows(MusicSnapshot music)
    {
        if (music.Queue.Count == 0)
        {
            yield return new TextNode(
                "Queue unavailable — reconnect Spotify in Accounts",
                theme.Text,
                theme.Text.MutedColor);
            yield break;
        }

        foreach (var (song, index) in music.Queue.Take(3).Select((song, index) => (song, index)))
        {
            yield return BuildQueueRow(music, song, index);
        }
    }

    private BoxNode BuildQueueRow(MusicSnapshot music, QueuedSong song, int position) => new(height: QUEUE_IMAGE_SIZE)
    {
        Direction = Direction.Horizontal,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Center,
        Style = Style.Spacer,
        Children =
        [
            BuildQueueCover(music, song, position),
            new BoxNode
            {
                Direction = Direction.Vertical,
                Style = new Style { Spacing = 2 },
                Children =
                [
                    new MarqueeTextNode(song.Title, 44, theme.Text, theme.Text),
                    new MarqueeTextNode(song.Artist, 50, 12.0f, theme.Text.MutedColor),
                ]
            },
            // BuildQueueRemoveButton(music, song, position),
        ]
    };

    private Node BuildQueueCover(MusicSnapshot music, QueuedSong song, int position)
    {
        var state = _queueCoverStates.GetState((song.Uri, position), Color.White with { A = 0 });
        state.Background = Color.LerpSmooth(
            state.Background,
            state.Hovered || string.IsNullOrWhiteSpace(song.ImagePath)
                ? Color.White
                : Color.White with { A = 0 },
            18.0f,
            Renderer.DeltaTime);

        return new BoxNode(QUEUE_IMAGE_SIZE, QUEUE_IMAGE_SIZE)
        {
            OnClick = () => _ = service.SkipToSpotifyQueuePositionAsync(music, position),
            Style = new Style { BorderRadius = 4 },
            Children =
            [
                string.IsNullOrWhiteSpace(song.ImagePath)
                    ? new BoxNode(QUEUE_IMAGE_SIZE, QUEUE_IMAGE_SIZE)
                    {
                        HorizontalAlignment = ItemsAlignment.Center,
                        VerticalAlignment = ItemsAlignment.Center,
                        Style = new Style { BackgroundColor = theme.Panel, BorderRadius = 4 },
                        Children = [new ImageNode(Icons.MusicNotes[0], 18, 18, theme.Text.MutedColor)]
                    }
                    : new ImageNode(song.ImagePath, QUEUE_IMAGE_SIZE, QUEUE_IMAGE_SIZE),
                new BoxNode(QUEUE_IMAGE_SIZE, QUEUE_IMAGE_SIZE)
                {
                    IgnoreLayout = true,
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    IsHovered = state.Hovered,
                    Style = new Style
                    {
                        BackgroundColor = Color.Black with
                        {
                            A = state.Background.A * (string.IsNullOrWhiteSpace(song.ImagePath) ? 0 : 0.6f)
                        },
                        BorderRadius = 4,
                    },
                    Children = [new ImageNode(Icons.Play, 18, 18, state.Background)]
                }
            ]
        };
    }

    private Node BuildQueueRemoveButton(MusicSnapshot music, QueuedSong song, int position)
    {
        var transparent = Color.White with { A = 0 };
        var state = _queueRemoveStates.GetState((song.Uri, position), transparent);
        state.Background = Color.LerpSmooth(
            state.Background,
            state.Hovered ? Color.White with { A = 0.3f } : transparent,
            18.0f,
            Renderer.DeltaTime);

        return new BoxNode(32, 32)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => _ = service.RemoveFromSpotifyQueueAsync(music, position),
            Style = new Style { BackgroundColor = state.Background, BorderRadius = 8 },
            Children = [new ImageNode(Icons.Delete, 16, 16, theme.Text)]
        };
    }

    private Node BuildPopupImage(MusicSnapshot music) =>
        string.IsNullOrWhiteSpace(music.ImagePath)
            ? new BoxNode(POPUP_IMAGE_SIZE, POPUP_IMAGE_SIZE)
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
                Children = [new TextNode("M", 28.0f, theme.Text.MutedColor)]
            }
            : new ImageNode(music.ImagePath, POPUP_IMAGE_SIZE, POPUP_IMAGE_SIZE);

    private Node BuildProgress(MusicSnapshot music)
    {
        var width = POPUP_WIDTH - POPUP_IMAGE_SIZE - 42;
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
                    music.Playing ? Color.Orange : Color.Orange with { A = 0.8f },
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
        _seekQueue.Submit(music.Bus, music.PositionMicros, _positionOverrideMicros);
    }

    private Node BuildControlButton(PlayerAction action, MusicSnapshot music)
    {
        var size = action == PlayerAction.PlayPause ? 44 : 36;
        var iconSize = action == PlayerAction.PlayPause ? 20 : 14;

        var active = action switch
        {
            PlayerAction.Shuffle => music.ShuffleEnabled == true,
            PlayerAction.Repeat => music.RepeatMode is not null and not MusicRepeatMode.Off,
            _ => false,
        };
        var defaultColor = action == PlayerAction.PlayPause || active ? theme.Active : theme.Panel;
        var state = _buttonStates.GetState(action, defaultColor);
        var target = state.Hovered
            ? Color.Lighten(defaultColor, 0.16f)
            : defaultColor;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, Renderer.DeltaTime);

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
                new ImageNode(action switch
                {
                    PlayerAction.PlayPause => music.Playing ? Icons.Pause : Icons.Play,
                    PlayerAction.Previous => Icons.SkipBack,
                    PlayerAction.Next => Icons.SkipForward,
                    PlayerAction.Shuffle => Icons.Shuffle,
                    PlayerAction.Repeat => music.RepeatMode == MusicRepeatMode.Track ? Icons.RepeatOne : Icons.Repeat,
                    _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
                }, iconSize, iconSize, theme.Text)
            ]
        };
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

    private void Control(MusicSnapshot music, PlayerAction action)
    {
        if (action == PlayerAction.Shuffle)
        {
            _ = service.ToggleSpotifyShuffleAsync(music);
            return;
        }

        if (action == PlayerAction.Repeat)
        {
            _ = service.CycleSpotifyRepeatAsync(music);
            return;
        }

        if (string.IsNullOrWhiteSpace(music.Bus))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await CommandRunner.TryReadAsync(
                "gdbus",
                $"call --session --dest {music.Bus} --object-path /org/mpris/MediaPlayer2 --method org.mpris.MediaPlayer2.Player.{action}",
                TimeSpan.FromMilliseconds(500),
                CancellationToken.None);
            await Task.Delay(150);
            await service.RefreshAsync();
        });
    }

    private sealed class SeekUpdateQueue
    {
        private readonly object _sync = new();
        private string _bus = "";
        private long _latestMicros;
        private long _sentMicros = -1;
        private bool _running;

        public void Submit(string bus, long currentMicros, long positionMicros)
        {
            lock (_sync)
            {
                if (!_running ||
                    !string.Equals(_bus, bus, StringComparison.Ordinal))
                {
                    _sentMicros = currentMicros;
                }

                _bus = bus;
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
                    positionMicros = _latestMicros;
                    previousMicros = _sentMicros;
                    _sentMicros = positionMicros;
                }

                if (string.IsNullOrWhiteSpace(bus))
                {
                    lock (_sync)
                    {
                        _running = false;
                    }
                    return;
                }

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
            }
        }
    }
}

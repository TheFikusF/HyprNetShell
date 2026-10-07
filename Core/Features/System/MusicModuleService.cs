using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Features.Spotify;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.System;

internal sealed class MusicModuleService : IDisposable
{
    private const string PLAYER_PATH = "/org/mpris/MediaPlayer2";
    private const string PLAYER_INTERFACE = "org.mpris.MediaPlayer2.Player";
    private const string PROPERTIES_INTERFACE = "org.freedesktop.DBus.Properties";

    private static readonly HttpClient Http = new() {
        Timeout = TimeSpan.FromSeconds(2),
    };
    private readonly SpotifyPlaybackService _spotify;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private DBusConnection? _connection;
    private IDisposable? _propertiesSubscription;
    private IDisposable? _seekedSubscription;
    private IDisposable? _nameOwnerSubscription;
    private MusicSnapshot _cached = MusicSnapshot.Empty;
    private bool _initialized;

    public MusicSnapshot Snapshot => _cached;

    internal Task RefreshAsync(CancellationToken cancellationToken = default) =>
        RefreshCachedAsync(cancellationToken, waitForGate: true);

    public MusicModuleService(SpotifyPlaybackService spotify)
    {
        _spotify = spotify;
        _spotify.AvailabilityChanged += HandleSpotifyAvailabilityChanged;
        _ = EnsureInitializedAsync(CancellationToken.None);
    }

    private void HandleSpotifyAvailabilityChanged() =>
        _ = RefreshCachedAsync(CancellationToken.None, waitForGate: true);

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var connection = new DBusConnection(Dbus.SessionAddress);
            await connection.ConnectAsync();
            _connection = connection;

            _propertiesSubscription = await AddSignalSubscriptionAsync(
                connection,
                new MatchRule {
                    Type = MessageType.Signal,
                    Path = PLAYER_PATH,
                    Interface = PROPERTIES_INTERFACE,
                    Member = "PropertiesChanged",
                });
            _seekedSubscription = await AddSignalSubscriptionAsync(
                connection,
                new MatchRule {
                    Type = MessageType.Signal,
                    Path = PLAYER_PATH,
                    Interface = PLAYER_INTERFACE,
                    Member = "Seeked",
                });
            _nameOwnerSubscription = await AddSignalSubscriptionAsync(
                connection,
                new MatchRule {
                    Type = MessageType.Signal,
                    Interface = Dbus.BUS_INTERFACE,
                    Member = "NameOwnerChanged",
                }, onlyMprisOwnerChanges: true);

            await RefreshCachedAsync(cancellationToken, waitForGate: true);
            _initialized = true;
        }
        catch
        {
            DisposeConnection();
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private ValueTask<IDisposable> AddSignalSubscriptionAsync(
        DBusConnection connection,
        MatchRule rule,
        bool onlyMprisOwnerChanges = false) =>
        connection.AddMatchAsync(
            rule,
            static (message, state) =>
            {
                var subscription = ((MusicModuleService Service, bool OnlyMprisOwnerChanges))state!;
                if (!subscription.OnlyMprisOwnerChanges)
                {
                    return true;
                }

                var reader = message.GetBodyReader();
                return reader.ReadString().StartsWith("org.mpris.MediaPlayer2.", StringComparison.Ordinal);
            },
            static notification =>
            {
                if (notification.HasValue && notification.Value)
                {
                    var subscription = ((MusicModuleService Service, bool OnlyMprisOwnerChanges))notification.State!;
                    _ = subscription.Service.RefreshCachedAsync(
                        CancellationToken.None,
                        waitForGate: true);
                }
            },
            false,
            ObserverFlags.None,
            (this, onlyMprisOwnerChanges));

    private async Task RefreshCachedAsync(CancellationToken cancellationToken, bool waitForGate)
    {
        if (waitForGate)
        {
            await _refreshGate.WaitAsync(cancellationToken);
        }
        else if (!await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return;
        }

        try
        {
            var info = await ReadMprisAsync(cancellationToken);
            if (info is null || string.IsNullOrWhiteSpace(info.Label))
            {
                _cached = MusicSnapshot.Empty;
                return;
            }

            var spotify = info.IsSpotify
                ? await _spotify.GetPlaybackAsync(cancellationToken)
                : null;
            _cached = info with {
                ImagePath = await LocalImagePathAsync(info.ArtUrl, cancellationToken),
                PositionObservedAtUtc = DateTime.UtcNow,
                Queue = spotify?.Queue ?? [],
                SpotifyCurrentUri = spotify?.CurrentUri,
                ShuffleEnabled = spotify?.ShuffleEnabled,
                RepeatMode = spotify?.RepeatMode,
            };
        }
        catch
        {
            // Keep the last event-derived state after transient player failures.
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<MusicInfo?> ReadMprisAsync(CancellationToken cancellationToken)
    {
        var namesOutput = await CommandRunner.TryReadAsync(
            "gdbus",
            "call --session --dest org.freedesktop.DBus --object-path /org/freedesktop/DBus --method org.freedesktop.DBus.ListNames",
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(namesOutput))
        {
            return null;
        }

        MusicInfo? best = null;
        foreach (var bus in ExtractMprisBusNames(namesOutput))
        {
            var status = await ReadMprisPropertyAsync(bus, "PlaybackStatus", cancellationToken);
            var metadata = await ReadMprisPropertyAsync(bus, "Metadata", cancellationToken);
            if (string.IsNullOrWhiteSpace(metadata))
            {
                continue;
            }

            var artist = ExtractMetadataArrayFirst(metadata, "xesam:artist");
            var album = ExtractMetadataString(metadata, "xesam:album");
            var title = ExtractMetadataString(metadata, "xesam:title");
            var artUrl = ExtractMetadataString(metadata, "mpris:artUrl");
            var length = ExtractMetadataInt64(metadata, "mpris:length");
            var position = ParseMprisInt64(await ReadMprisPropertyAsync(bus, "Position", cancellationToken));
            var label = FormatLabel(artist, title);
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            var candidate = new MusicInfo(
                bus,
                bus.StartsWith("org.mpris.MediaPlayer2.", StringComparison.Ordinal)
                    ? bus["org.mpris.MediaPlayer2.".Length..]
                    : bus,
                artist,
                album,
                title,
                label,
                artUrl,
                null,
                status?.Contains("Playing", StringComparison.OrdinalIgnoreCase) == true,
                length,
                position);
            if (best is null ||
                candidate.Playing && !best.Playing ||
                candidate.Playing == best.Playing && bus.Contains("spotify", StringComparison.OrdinalIgnoreCase))
            {
                best = candidate;
            }
        }

        return best;
    }

    internal async Task ToggleSpotifyShuffleAsync(MusicSnapshot music, CancellationToken cancellationToken = default)
    {
        if (!music.IsSpotify || music.ShuffleEnabled is not bool enabled)
        {
            return;
        }

        if (await _spotify.ToggleShuffleAsync(enabled, cancellationToken))
        {
            _cached = _cached with {
                ShuffleEnabled = !enabled
            };
            await RefreshCachedAsync(cancellationToken, waitForGate: true);
        }
    }

    internal async Task CycleSpotifyRepeatAsync(MusicSnapshot music, CancellationToken cancellationToken = default)
    {
        if (!music.IsSpotify || music.RepeatMode is not MusicRepeatMode repeatMode)
        {
            return;
        }

        if (await _spotify.CycleRepeatAsync(repeatMode, cancellationToken) is { } next)
        {
            _cached = _cached with {
                RepeatMode = next
            };
            await RefreshCachedAsync(cancellationToken, waitForGate: true);
        }
    }

    internal async Task SkipToSpotifyQueuePositionAsync(
        MusicSnapshot music,
        int position,
        CancellationToken cancellationToken = default)
    {
        if (!music.IsSpotify || position < 0 || position >= music.Queue.Count)
        {
            return;
        }

        if (await _spotify.SkipToQueuePositionAsync(position, cancellationToken))
        {
            await RefreshCachedAsync(cancellationToken, waitForGate: true);
        }
    }

    internal async Task RemoveFromSpotifyQueueAsync(
        MusicSnapshot music,
        int position,
        CancellationToken cancellationToken = default)
    {
        if (!music.IsSpotify ||
            string.IsNullOrWhiteSpace(music.SpotifyCurrentUri) ||
            position < 0 ||
            position >= music.Queue.Count)
        {
            return;
        }

        if (await _spotify.RemoveFromQueueAsync(
                music.SpotifyCurrentUri,
                EffectivePosition(music),
                music.Playing,
                music.Queue,
                position,
                cancellationToken))
        {
            await RefreshCachedAsync(cancellationToken, waitForGate: true);
        }
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

    private static Task<string?> ReadMprisPropertyAsync(
        string bus,
        string property,
        CancellationToken cancellationToken) =>
        CommandRunner.TryReadAsync(
            "gdbus",
            $"call --session --dest {bus} --object-path /org/mpris/MediaPlayer2 --method org.freedesktop.DBus.Properties.Get org.mpris.MediaPlayer2.Player {property}",
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

    private static IEnumerable<string> ExtractMprisBusNames(string output)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(output, @"org\.mpris\.MediaPlayer2\.[A-Za-z0-9_.-]+"))
        {
            if (seen.Add(match.Value))
            {
                yield return match.Value;
            }
        }
    }

    private static string ExtractMetadataString(string metadata, string key)
    {
        var match = Regex.Match(
            metadata,
            $@"'{Regex.Escape(key)}'\s*:\s*<(?:(?:@s\s*)?'(?<value>(?:\\.|[^'])*)'|.*?""(?<value>[^""]*)"")>",
            RegexOptions.Singleline);
        return match.Success ? UnescapeGVariantString(match.Groups["value"].Value) : "";
    }

    private static string ExtractMetadataArrayFirst(string metadata, string key)
    {
        var match = Regex.Match(
            metadata,
            $@"'{Regex.Escape(key)}'\s*:\s*<\[\s*'(?<value>(?:\\.|[^'])*)'",
            RegexOptions.Singleline);
        return match.Success ? UnescapeGVariantString(match.Groups["value"].Value) : "";
    }

    private static long ExtractMetadataInt64(string metadata, string key)
    {
        var match = Regex.Match(
            metadata,
            $@"'{Regex.Escape(key)}'\s*:\s*<(?:(?:int64|uint64|int32|uint32)\s+)?(?<value>-?\d+)>",
            RegexOptions.Singleline);
        return match.Success ? ParseLong(match.Groups["value"].Value) : 0;
    }

    private static long ParseMprisInt64(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        var match = Regex.Match(output, @"(?:int64|uint64|int32|uint32)\s+(?<value>-?\d+)|<(?<value>-?\d+)>");
        return match.Success ? ParseLong(match.Groups["value"].Value) : 0;
    }

    private static long ParseLong(string? value) =>
        long.TryParse(value?.Trim(), out var parsed) ? parsed : 0;

    private static string FormatLabel(string artist, string title)
    {
        return (artist, title) switch {
            ( { Length: > 0 }, { Length: > 0 }) => $"{artist} - {title}",
            (_, { Length: > 0 }) => title,
            ( { Length: > 0 }, _) => artist,
            _ => "",
        };
    }

    private static string UnescapeGVariantString(string value) =>
        value
            .Replace(@"\'", "'", StringComparison.Ordinal)
            .Replace(@"\""", "\"", StringComparison.Ordinal)
            .Replace(@"\\", @"\", StringComparison.Ordinal);

    private static async Task<string?> LocalImagePathAsync(string artUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artUrl))
        {
            return null;
        }

        if (Uri.TryCreate(artUrl, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            return uri.LocalPath;
        }

        if (File.Exists(artUrl))
        {
            return artUrl;
        }

        if (Uri.TryCreate(artUrl, UriKind.Absolute, out uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await CacheRemoteArtAsync(uri, cancellationToken);
        }

        return null;
    }

    private static async Task<string?> CacheRemoteArtAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
            {
                extension = ".jpg";
            }

            var cacheDir = Path.Combine(Path.GetTempPath(), "HyprNetShell", "mpris-art");
            Directory.CreateDirectory(cacheDir);
            var hash = Convert.ToHexString(SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            var path = Path.Combine(cacheDir, hash + extension);
            if (File.Exists(path))
            {
                return path;
            }

            await using var stream = await Http.GetStreamAsync(uri, cancellationToken);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file, cancellationToken);
            return path;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        _spotify.AvailabilityChanged -= HandleSpotifyAvailabilityChanged;
        DisposeConnection();
        _initializeGate.Dispose();
        _refreshGate.Dispose();
    }

    private void DisposeConnection()
    {
        _propertiesSubscription?.Dispose();
        _propertiesSubscription = null;
        _seekedSubscription?.Dispose();
        _seekedSubscription = null;
        _nameOwnerSubscription?.Dispose();
        _nameOwnerSubscription = null;
        _connection?.Dispose();
        _connection = null;
    }



    private sealed record MusicInfo(
        string Bus,
        string Player,
        string Artist,
        string Album,
        string Title,
        string Label,
        string ArtUrl,
        string? ImagePath,
        bool Playing,
        long LengthMicros,
        long PositionMicros)
        : MusicSnapshot(Bus, Player, Artist, Album, Title, Label, ArtUrl, ImagePath, Playing, LengthMicros, PositionMicros);
}

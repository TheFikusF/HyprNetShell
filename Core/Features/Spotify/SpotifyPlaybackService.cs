using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using HyprNetShell.Core.Features.OnlineAccounts;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.Spotify;

internal sealed record SpotifyPlaybackSnapshot(
    IReadOnlyList<QueuedSong> Queue,
    string? CurrentUri,
    bool? ShuffleEnabled,
    MusicRepeatMode? RepeatMode);

internal sealed class SpotifyPlaybackService : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(3),
    };

    private readonly OnlineAccountsService _onlineAccounts;

    internal event Action? AvailabilityChanged;

    internal SpotifyPlaybackService(OnlineAccountsService onlineAccounts)
    {
        _onlineAccounts = onlineAccounts;
        _onlineAccounts.AccountChanged += HandleAccountChanged;
    }

    internal async Task<SpotifyPlaybackSnapshot?> GetPlaybackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await _onlineAccounts.GetSpotifyAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return null;
            }

            using var playbackRequest = CreateRequest(HttpMethod.Get, "https://api.spotify.com/v1/me/player", token);
            using var queueRequest = CreateRequest(HttpMethod.Get, "https://api.spotify.com/v1/me/player/queue", token);
            using var playbackResponse = await Http.SendAsync(playbackRequest, cancellationToken);
            using var queueResponse = await Http.SendAsync(queueRequest, cancellationToken);
            if (!playbackResponse.IsSuccessStatusCode || !queueResponse.IsSuccessStatusCode)
            {
                if (!playbackResponse.IsSuccessStatusCode)
                {
                    await LogHttpFailureAsync("read playback state", playbackResponse, cancellationToken);
                }
                if (!queueResponse.IsSuccessStatusCode)
                {
                    await LogHttpFailureAsync("read queue", queueResponse, cancellationToken);
                }
                return null;
            }

            var playback = await playbackResponse.Content.ReadFromJsonAsync(
                SpotifyPlaybackJsonContext.Default.SpotifyPlaybackState,
                cancellationToken);
            var queue = await queueResponse.Content.ReadFromJsonAsync(
                SpotifyPlaybackJsonContext.Default.SpotifyQueueResponse,
                cancellationToken);
            var songs = await Task.WhenAll(
                (queue?.Queue ?? [])
                    .Select(item => ToQueuedSongAsync(item, cancellationToken)));

            return new SpotifyPlaybackSnapshot(
                songs,
                queue?.CurrentlyPlaying?.Uri,
                playback?.ShuffleState,
                ParseRepeatMode(playback?.RepeatState));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Spotify", "Could not read Spotify playback state", exception);
            return null;
        }
    }

    internal async Task<bool> ToggleShuffleAsync(bool enabled, CancellationToken cancellationToken = default) =>
        await SendControlAsync($"shuffle?state={!enabled}".ToLowerInvariant(), cancellationToken);

    internal async Task<bool> SkipToQueuePositionAsync(
        int position,
        CancellationToken cancellationToken = default)
    {
        for (var index = 0; index <= position; index++)
        {
            if (!await SendControlAsync("next", cancellationToken, HttpMethod.Post))
            {
                return false;
            }
        }

        return true;
    }

    internal async Task<bool> RemoveFromQueueAsync(
        string currentUri,
        long positionMicros,
        bool playing,
        IReadOnlyList<QueuedSong> queue,
        int position,
        CancellationToken cancellationToken = default)
    {
        if (position < 0 || position >= queue.Count)
        {
            return false;
        }

        var uris = new[] { currentUri }
            .Concat(queue.Where((_, index) => index != position).Select(song => song.Uri))
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .ToArray();
        if (uris.Length == 0)
        {
            return false;
        }

        var request = new SpotifyResumeRequest(uris, Math.Max(0, positionMicros / 1000));
        if (!await SendControlAsync("play", cancellationToken, HttpMethod.Put, request))
        {
            return false;
        }

        return playing || await SendControlAsync("pause", cancellationToken);
    }

    internal async Task<MusicRepeatMode?> CycleRepeatAsync(
        MusicRepeatMode repeatMode,
        CancellationToken cancellationToken = default)
    {
        var next = repeatMode switch
        {
            MusicRepeatMode.Off => MusicRepeatMode.Context,
            MusicRepeatMode.Context => MusicRepeatMode.Track,
            MusicRepeatMode.Track => MusicRepeatMode.Off,
            _ => MusicRepeatMode.Off,
        };
        var state = next switch
        {
            MusicRepeatMode.Context => "context",
            MusicRepeatMode.Track => "track",
            _ => "off",
        };
        return await SendControlAsync($"repeat?state={state}", cancellationToken) ? next : null;
    }

    private async Task<bool> SendControlAsync(
        string pathAndQuery,
        CancellationToken cancellationToken,
        HttpMethod? method = null,
        SpotifyResumeRequest? body = null)
    {
        try
        {
            var token = await _onlineAccounts.GetSpotifyAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            using var request = CreateRequest(
                method ?? HttpMethod.Put,
                $"https://api.spotify.com/v1/me/player/{pathAndQuery}",
                token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, SpotifyPlaybackJsonContext.Default.SpotifyResumeRequest);
            }
            using var response = await Http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            await LogHttpFailureAsync($"update playback ({pathAndQuery})", response, cancellationToken);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Spotify", $"Could not update Spotify playback ({pathAndQuery})", exception);
            return false;
        }
    }

    private static async Task<QueuedSong> ToQueuedSongAsync(
        SpotifyQueueItem item,
        CancellationToken cancellationToken)
    {
        var imageUrl = item.Album?.Images?.LastOrDefault()?.Url
            ?? item.Images?.LastOrDefault()?.Url;
        return new QueuedSong(
            item.Uri ?? "",
            item.Name ?? "Unknown title",
            item.Artists?.FirstOrDefault()?.Name ?? item.Show?.Name ?? "Unknown artist",
            await CacheAlbumArtAsync(imageUrl, cancellationToken));
    }

    private static async Task<string?> CacheAlbumArtAsync(string? imageUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        try
        {
            var extension = Path.GetExtension(uri.AbsolutePath);
            if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
            {
                extension = ".jpg";
            }

            var cacheDirectory = Path.Combine(Path.GetTempPath(), "HyprNetShell", "spotify-art");
            Directory.CreateDirectory(cacheDirectory);
            var hash = Convert.ToHexString(SHA256.HashData(global::System.Text.Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
            var path = Path.Combine(cacheDirectory, hash + extension);
            if (File.Exists(path))
            {
                return path;
            }

            await using var stream = await Http.GetStreamAsync(uri, cancellationToken);
            await using var file = File.Create(path);
            await stream.CopyToAsync(file, cancellationToken);
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Spotify", $"Could not cache Spotify album art from {uri.Host}", exception);
            return null;
        }
    }

    private static async Task LogHttpFailureAsync(
        string operation,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string? detail = null;
        try
        {
            detail = await response.Content.ReadAsStringAsync(cancellationToken);
            if (detail.Length > 512)
            {
                detail = detail[..512];
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning("Spotify", $"Could not read the error response while trying to {operation}", exception);
        }

        var suffix = string.IsNullOrWhiteSpace(detail) ? "" : $"; response={detail}";
        AppLogger.Warning(
            "Spotify",
            $"Failed to {operation}: status={(int)response.StatusCode} {response.ReasonPhrase}{suffix}");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string uri, string token)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static MusicRepeatMode? ParseRepeatMode(string? state) => state switch
    {
        "off" => MusicRepeatMode.Off,
        "context" => MusicRepeatMode.Context,
        "track" => MusicRepeatMode.Track,
        _ => null,
    };

    private void HandleAccountChanged(OnlineAccountProvider provider)
    {
        if (provider == OnlineAccountProvider.Spotify)
        {
            AvailabilityChanged?.Invoke();
        }
    }

    public void Dispose() => _onlineAccounts.AccountChanged -= HandleAccountChanged;
}

internal sealed record SpotifyPlaybackState(
    [property: JsonPropertyName("shuffle_state")] bool ShuffleState,
    [property: JsonPropertyName("repeat_state")] string? RepeatState);

internal sealed record SpotifyQueueResponse(
    [property: JsonPropertyName("currently_playing")] SpotifyQueueItem? CurrentlyPlaying,
    [property: JsonPropertyName("queue")] SpotifyQueueItem[] Queue);

internal sealed record SpotifyQueueItem(
    [property: JsonPropertyName("uri")] string? Uri,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("artists")] SpotifyArtist[]? Artists,
    [property: JsonPropertyName("album")] SpotifyAlbum? Album,
    [property: JsonPropertyName("images")] SpotifyImage[]? Images,
    [property: JsonPropertyName("show")] SpotifyShow? Show);

internal sealed record SpotifyArtist(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record SpotifyAlbum(
    [property: JsonPropertyName("images")] SpotifyImage[]? Images);

internal sealed record SpotifyImage(
    [property: JsonPropertyName("url")] string? Url);

internal sealed record SpotifyShow(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record SpotifyResumeRequest(
    [property: JsonPropertyName("uris")] string[] Uris,
    [property: JsonPropertyName("position_ms")] long PositionMs);

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SpotifyPlaybackState))]
[JsonSerializable(typeof(SpotifyResumeRequest))]
[JsonSerializable(typeof(SpotifyQueueResponse))]
internal sealed partial class SpotifyPlaybackJsonContext : JsonSerializerContext;

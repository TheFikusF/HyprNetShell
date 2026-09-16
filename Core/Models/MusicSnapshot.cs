namespace HyprNetShell.Core.Models;

public enum MusicRepeatMode
{
    Off,
    Context,
    Track,
}

public sealed record QueuedSong(string Uri, string Title, string Artist, string? ImagePath);

public record MusicSnapshot(
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
{
    public static MusicSnapshot Empty { get; } = new("", "", "", "", "", "", "", null, false, 0, 0);
    public bool Available => !string.IsNullOrWhiteSpace(Label);
    public bool IsSpotify => Bus.Contains("spotify", StringComparison.OrdinalIgnoreCase);
    public DateTime PositionObservedAtUtc { get; init; } = DateTime.UtcNow;
    public IReadOnlyList<QueuedSong> Queue { get; init; } = [];
    public string? SpotifyCurrentUri { get; init; }
    public bool? ShuffleEnabled { get; init; }
    public MusicRepeatMode? RepeatMode { get; init; }
}

namespace HyprNetShell.Core.Models;

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
    public DateTime PositionObservedAtUtc { get; init; } = DateTime.UtcNow;
}

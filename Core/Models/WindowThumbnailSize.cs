namespace HyprNetShell.Core.Models;

/// <summary>A positive capture bounding box, capped at the native thumbnail limit.</summary>
public readonly record struct WindowThumbnailSize
{
    public const int MAX_WIDTH = 640;
    public const int MAX_HEIGHT = 360;

    public static WindowThumbnailSize Default => new(MAX_WIDTH, MAX_HEIGHT);
    public int Width { get; }
    public int Height { get; }

    public WindowThumbnailSize(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Width = Math.Min(width, MAX_WIDTH);
        Height = Math.Min(height, MAX_HEIGHT);
    }
}

using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

public sealed record Theme
{
    public float TextSize { get; init; }
    public float BorderRadius { get; init; }
    public float BorderWidth { get; init; }
    public Color Panel { get; init; }
    public Color Active { get; init; }
    public Color Warning { get; init; }
    public Color Critical { get; init; }
    public Color Muted { get; init; }
    public Color Border { get; init; }
    public Color Text { get; init; }

    public static Theme Default { get; } = new()
    {
        TextSize = 14.0f,
        BorderRadius = 4308,
        BorderWidth = 3.0f,
        Border = Color.White,
        Panel = Color.FromRgb(31, 35, 44, 0.9f),
        Active = Color.Lerp(Color.FromRgb(31, 35, 44, 0.92f), Color.Orange, 0.5f),
        Warning = Color.FromRgb(230, 126, 34, 0.85f),
        Critical = Color.FromRgb(231, 76, 60, 0.90f),
        Muted = Color.FromRgb(96, 96, 96),
        Text = Color.FromRgb(255, 255, 255)
    };
}
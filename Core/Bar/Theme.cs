using HyprNetShell.Rendering.Primitives;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Bar;

public sealed record Theme
{
    public readonly struct TextParams
    {
        public float Size { get; init; }
        public float HeaderSize { get; init; }
        public Color Color { get; init; }
        public Color MutedColor { get; init; }

        public static implicit operator Color(TextParams value) => value.Color;
        public static implicit operator float(TextParams value) => value.Size;
    }

    public readonly struct BorderParams
    {
        public float Radius { get; init; }
        public float Width { get; init; }
        public Color Color { get; init; }

        public static implicit operator Color(BorderParams value) => value.Color;
    }

    public Color Panel { get; init; }
    public Color Active { get; init; }
    public Color Warning { get; init; }
    public Color Critical { get; init; }

    public BorderParams Border { get; init; }
    public TextParams Text { get; init; }

    public static Theme Default { get; } = new()
    {
        Panel = Color.FromRgb(31, 35, 44, 0.9f),
        Active = Color.Lerp(Color.FromRgb(31, 35, 44, 0.92f), Color.Orange, 0.5f),
        Warning = Color.FromRgb(230, 126, 34, 0.9f),
        Critical = Color.FromRgb(231, 76, 60, 0.9f),
        Text = new TextParams
        {
            Color = Color.White,
            Size = 14.0f,
            HeaderSize = 18.0f,
            MutedColor = Color.FromRgb(128, 128, 128),
        },
        Border = new BorderParams
        {
            Radius = 4308,
            Width = 3.0f,
            Color = Color.White,
        }
    };
}

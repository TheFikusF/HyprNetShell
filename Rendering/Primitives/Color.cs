using System.Globalization;

namespace HyprNetShell.Rendering.Primitives;

public readonly record struct Color(float R, float G, float B, float A)
{
    public static Color FromRgb(byte r, byte g, byte b, float a = 1.0f) => new(r / 255.0f, g / 255.0f, b / 255.0f, a);

    public static Color FromHex(string hex) => hex.Length == 7 || hex.Length == 9
        ? FromRgb(byte.Parse(hex[1..3], NumberStyles.HexNumber), 
            byte.Parse(hex[3..5], NumberStyles.HexNumber), 
            byte.Parse(hex[5..7], NumberStyles.HexNumber),
            hex.Length == 9 ? float.Parse(hex[7..9], NumberStyles.HexNumber) / 255.0f : 1.0f)
        : throw new FormatException();

    public static Color Lighten(Color color, float amount) => PrimitivesMath.Lighten(color, amount);
    public static Color Darken(Color color, float amount) => PrimitivesMath.Darken(color, amount);
    public static Color Lerp(Color a, Color b, float t) => PrimitivesMath.Lerp(a, b, t);

    public static Color LerpSmooth(Color a, Color b, float decay, float dt) => a.LerpSmooth(b, decay, dt);
    public Color LerpSmooth(Color b, float decay, float dt) => PrimitivesMath.LerpSmooth(this, b, decay, dt);

    public static readonly Color Red = FromRgb(255, 0, 0);
    public static readonly Color Green = FromRgb(0, 255, 0);
    public static readonly Color Blue = FromRgb(0, 0, 255);
    public static readonly Color Yellow = FromRgb(255, 255, 0);
    public static readonly Color Orange = FromRgb(255, 128, 0);
    public static readonly Color Violet = FromRgb(255, 0, 255);
    public static readonly Color Lazure = FromRgb(0, 255, 255);
    public static readonly Color White = FromRgb(255, 255, 255);
    public static readonly Color Black = FromRgb(0, 0, 0);
    
    public Color PushOpacity(float opacity) => this with { A = A * opacity };
}
using System.Runtime.CompilerServices;

namespace HyprNetShell.Rendering.Primitives;

public static class PrimitivesMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color Lerp(Color a, Color b, float amount) =>
        new(Lerp(a.R, b.R, amount), 
            Lerp(a.G, b.G, amount), 
            Lerp(a.B, b.B, amount),
            Lerp(a.A, b.A, amount));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float LerpSmooth(float a, float b, float decay, float dt) => (a - b) * MathF.Exp(-decay * dt) + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color LerpSmooth(Color a, Color b, float decay, float dt) =>
        new(
            LerpSmooth(a.R, b.R, decay, dt),
            LerpSmooth(a.G, b.G, decay, dt),
            LerpSmooth(a.B, b.B, decay, dt),
            LerpSmooth(a.A, b.A, decay, dt));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color Lighten(Color color, float amount) =>
        new(
            color.R + (1.0f - color.R) * amount,
            color.G + (1.0f - color.G) * amount,
            color.B + (1.0f - color.B) * amount,
            color.A);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Color Darken(Color color, float amount) =>
        new(
            color.R - color.R * amount,
            color.G - color.G * amount,
            color.B - color.B * amount,
            color.A);
}
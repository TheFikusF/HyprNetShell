using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

public sealed record StatusBarTheme(
    float ModuleTop,
    float ModuleBottom,
    float Radius,
    float BorderWidth,
    Color Background,
    Color Panel,
    Color Active,
    Color Warning,
    Color Critical,
    Color Muted,
    Color Border,
    Color Text)
{
    public static StatusBarTheme Default { get; } = new(
        ModuleTop: 7.0f,
        ModuleBottom: 7.0f,
        Radius: 32.0f,
        BorderWidth: 3.0f,
        Background: Color.FromRgb(13, 17, 23, 0.86f),
        Panel: Color.FromRgb(31, 35, 44, 0.92f),
        Active: Color.FromRgb(82, 152, 245, 0.95f),
        Warning: Color.FromRgb(230, 126, 34, 0.85f),
        Critical: Color.FromRgb(231, 76, 60, 0.90f),
        Muted: Color.FromRgb(80, 80, 80, 0.70f),
        Border: Color.FromRgb(255, 255, 255),
        Text: Color.FromRgb(255, 255, 255));
}

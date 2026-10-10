using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering.Renderers;

public sealed unsafe partial class Renderer
{
    private readonly FontRenderer _font;

    public float MeasureText(string text, float fontSize) => _font.MeasureText(text, fontSize);

    public void DrawText(string text, float x, float y, float fontSize, Color color, float charDistance)
    {
        RecordTextDraw();
        FlushPendingGeometry();
        _font.DrawText(text, x, y, fontSize, charDistance, color);
    }
}

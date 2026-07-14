using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public class TextNode : Node
{
    public override int Width =>
        (int)MathF.Ceiling(Layout.Renderer.MeasureText(Text, FontSize) + Style.Padding.Left + Style.Padding.Right);

    public override int Height => (int)MathF.Ceiling(FontSize + Style.Padding.Top + Style.Padding.Bottom);

    private string Text { get; }
    private float FontSize { get; }
    private Color Color { get; }

    public Color? ShadowColor { get; init; }
    public float ShadowDistance { get; init; }

    public TextNode(string text, float fontSize = 14.0f, Color? color = null)
    {
        Text = text;
        FontSize = fontSize;
        Color = color ?? Color.FromRgb(255, 255, 255);
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
        Layout.AddInputRegion(new Rect(x, y, Width, Height));

        if (ShadowColor.HasValue)
        {
            renderer.DrawText(Text, Style.Padding.Left + x,
                Style.Padding.Top + y + (int)(FontSize * 0.8f) + (int)ShadowDistance, FontSize, ShadowColor.Value.PushOpacity(Opacity), 0);
        }

        renderer.DrawText(Text, Style.Padding.Left + x, Style.Padding.Top + y + (int)(FontSize * 0.8f), 
            FontSize, Color.PushOpacity(Opacity));
    }
}
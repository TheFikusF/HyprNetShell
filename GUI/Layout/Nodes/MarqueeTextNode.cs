using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public class MarqueeTextNode : Node
{
    private const int GAP_CHARACTERS = 6;
    private const double CHARACTERS_PER_SECOND = 8.0;

    private readonly string _text;
    private readonly float _fontSize;
    private readonly Color _color;
    private readonly int _visibleCharacters;
    
    public Color? ShadowColor { get; init; }
    public float ShadowDistance { get; init; }

    public override int Width => (int)MathF.Ceiling(Layout.Renderer.MeasureText(VisibleText(), _fontSize) + Style.Padding.Left + Style.Padding.Right);
    public override int Height => (int)MathF.Ceiling(_fontSize + Style.Padding.Top + Style.Padding.Bottom);

    public MarqueeTextNode(
        string text,
        int visibleCharacters = 50,
        float fontSize = 14.0f,
        Color? color = null)
    {
        _text = text;
        _visibleCharacters = Math.Max(1, visibleCharacters);
        _fontSize = fontSize;
        _color = color ?? Color.FromRgb(255, 255, 255);
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
        Layout.AddInputRegion(new Rect(x, y, Width, Height));

        var text = VisibleText();
        
        if (ShadowColor.HasValue)
        {
            renderer.DrawText(text, Style.Padding.Left + x,
                Style.Padding.Top + y + (int)(_fontSize * 0.8f) + (int)ShadowDistance, _fontSize, ShadowColor.Value, 0);
        }
        
        renderer.DrawText(text, Style.Padding.Left + x, Style.Padding.Top + y + (int)(_fontSize * 0.8f), _fontSize, _color);
    }

    private string VisibleText()
    {
        if (_text.Length <= _visibleCharacters)
        {
            return _text;
        }

        var gap = new string(' ', GAP_CHARACTERS);
        var tape = _text + gap;
        var offset = (int)(Environment.TickCount64 / 1000.0 * CHARACTERS_PER_SECOND) % tape.Length;
        return string.Create(_visibleCharacters, (tape, offset), static (span, state) =>
        {
            var (source, start) = state;
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = source[(start + i) % source.Length];
            }
        });
    }
}

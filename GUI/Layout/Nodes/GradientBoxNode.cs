using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public class GradientBoxNode : BoxNode
{
    private readonly Func<float> _offset;
    private readonly Color _left;
    private readonly Color _right;

    public GradientBoxNode(Color left, Color right, Func<float> offset, int? width = null, int? height = null)
        : base(width, height)
    {
        _left = left;
        _right = right;
        _offset = offset;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var style = Style;
        var rect = new Rect(x, y, Width, Height);
        var gradientRect = style.BorderColor.HasValue && style.BorderWidth.Max > 0.0f
            ? rect.Inset(style.BorderWidth)
            : rect;
        var gradientRadius = style.BorderColor.HasValue && style.BorderWidth.Max > 0.0f
            ? style.BorderRadius.Inset(style.BorderWidth)
            : style.BorderRadius;

        if (style.BorderColor.HasValue)
        {
            renderer.FillRoundedRect(rect, style.BorderRadius, style.BorderColor.Value);
        }

        renderer.FillRoundedRectHorizontalGradient(
            gradientRect,
            gradientRadius,
            _left,
            _right,
            _offset());

        Layout.AddInputRegion(rect);
        Style = style with { BackgroundColor = null, BorderColor = null };
        base.Draw(renderer, x, y);
        Style = style;
    }
}

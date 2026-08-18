using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using PrimitiveGradientDirection = HyprNetShell.Rendering.Primitives.GradientDirection;

namespace HyprNetShell.GUI.Layout.Nodes;

public class GradientBoxNode : BoxNode
{
    private readonly Func<float> _offset;
    private readonly Gradient _gradient;

    public PrimitiveGradientDirection GradientDirection { get; init; } = PrimitiveGradientDirection.Horizontal;

    public GradientBoxNode(Color left, Color right, Func<float> offset, int? width = null, int? height = null)
        : this(
            new Gradient(
                new Gradient.Stop(0.0f, left),
                new Gradient.Stop(0.5f, right),
                new Gradient.Stop(1.0f, left)),
            offset,
            width,
            height)
    {
    }

    public GradientBoxNode(Gradient gradient, int? width = null, int? height = null)
        : this(gradient, static () => 0.0f, width, height)
    {
    }

    public GradientBoxNode(Gradient gradient, Func<float> offset, int? width = null, int? height = null)
        : base(width, height)
    {
        ArgumentNullException.ThrowIfNull(gradient);
        ArgumentNullException.ThrowIfNull(offset);

        _gradient = gradient;
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
            renderer.FillRoundedBorder(rect, style.BorderRadius, style.BorderWidth, style.BorderColor.Value);
        }

        renderer.FillRoundedRectGradient(
            gradientRect,
            gradientRadius,
            _gradient,
            GradientDirection,
            _offset());

        Layout.AddInputRegion(rect);
        Style = style with { BackgroundColor = null, BorderColor = null };
        base.Draw(renderer, x, y);
        Style = style;
    }
}

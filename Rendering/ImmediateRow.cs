using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering;

public sealed class ImmediateRow
{
    private readonly IRenderApi _render;
    private readonly float _gap;
    private float _left;
    private float _right;

    public ImmediateRow(IRenderApi render, Rect bounds, float gap)
    {
        _render = render;
        Bounds = bounds;
        _gap = gap;
        _left = bounds.X;
        _right = bounds.X + bounds.Width;
    }

    public Rect Bounds { get; }

    public Rect TakeLeft(float width)
    {
        var rect = new Rect(_left, Bounds.Y, MathF.Max(0, width), Bounds.Height);
        _left += rect.Width + _gap;
        return rect;
    }

    public Rect TakeLeftText(string text, float fontSize, float horizontalPadding, float minWidth = 0)
    {
        return TakeLeft(MathF.Max(minWidth, _render.MeasureText(text, fontSize) + horizontalPadding * 2.0f));
    }

    public Rect TakeRight(float width)
    {
        width = MathF.Max(0, width);
        _right -= width;
        var rect = new Rect(_right, Bounds.Y, width, Bounds.Height);
        _right -= _gap;
        return rect;
    }

    public Rect TakeRightText(string text, float fontSize, float horizontalPadding, float minWidth = 0)
    {
        return TakeRight(MathF.Max(minWidth, _render.MeasureText(text, fontSize) + horizontalPadding * 2.0f));
    }

    public Rect Center(float width)
    {
        width = MathF.Max(0, MathF.Min(width, Bounds.Width));
        return new Rect(Bounds.X + (Bounds.Width - width) * 0.5f, Bounds.Y, width, Bounds.Height);
    }
}

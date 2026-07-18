namespace HyprNetShell.Rendering.Primitives;

public readonly record struct Rect(float X, float Y, float Width, float Height)
{
    public bool Contains(float x, float y) =>
        x >= X &&
        y >= Y &&
        x < X + Width &&
        y < Y + Height;

    public Rect Inset(Insets width)
    {
        return new Rect(
            X + width.Left,
            Y + width.Top,
            MathF.Max(0.0f, Width - width.Left - width.Right),
            MathF.Max(0.0f, Height - width.Top - width.Bottom));
    }
}

public readonly record struct BorderRadius(float TopLeft, float TopRight, float BottomRight, float BottomLeft)
{
    public BorderRadius(float radius) : this(radius, radius, radius, radius)
    {
    }

    public static implicit operator BorderRadius(float radius) => new(radius);

    public BorderRadius Inset(Insets width)
    {
        return new BorderRadius(
            MathF.Max(0.0f, TopLeft - MathF.Max(width.Top, width.Left)),
            MathF.Max(0.0f, TopRight - MathF.Max(width.Top, width.Right)),
            MathF.Max(0.0f, BottomRight - MathF.Max(width.Bottom, width.Right)),
            MathF.Max(0.0f, BottomLeft - MathF.Max(width.Bottom, width.Left)));
    }
}

public readonly record struct Insets(float Top, float Right, float Bottom, float Left)
{
    public Insets(float amount) : this(amount, amount, amount, amount)
    {
    }

    public Insets(float horizontal, float vertical) : this(vertical, horizontal, vertical, horizontal)
    {
    }

    public static implicit operator Insets(float amount) => new(amount);

    public float Max => MathF.Max(MathF.Max(Top, Right), MathF.Max(Bottom, Left));
}
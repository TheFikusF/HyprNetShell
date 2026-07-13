using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout;

public enum ItemsAlignment
{
    Start,
    Center,
    End,
    Spread,
    Stretch,
}

public enum Direction
{
    Horizontal,
    Vertical,
}

public struct Style
{
    public Insets Padding;
    public int Spacing;
    public Color? BackgroundColor;
    public Color? BorderColor;
    public BorderRadius BorderRadius;
    public Insets BorderWidth;
}

public abstract class Node
{
    public virtual int Width { get; }
    public virtual int Height { get; }
    public Style Style { get; set; } = new Style();
    internal bool LastHovered { get; private set; }
    internal bool LastHoveredThrough { get; private set; }
    internal bool LastClicked { get; private set; }
    internal bool LastClickedThrough { get; private set; }
    internal bool LastHoveredInTree => LastHovered || LastHoveredThrough;
    internal bool LastClickedInTree => LastClicked || LastClickedThrough;

    public abstract void Draw(IRenderApi renderer, int x, int y);

    protected void UpdateInteractionState(int x, int y)
    {
        var hovered = Layout.Input.Contains(new Rect(x, y, Width, Height));
        SetInteractionState(hovered, false, hovered && Layout.Input.PointerPressed, false);
    }

    protected void SetInteractionState(bool hovered, bool hoveredThrough, bool clicked, bool clickedThrough)
    {
        LastHovered = hovered;
        LastHoveredThrough = hoveredThrough;
        LastClicked = clicked;
        LastClickedThrough = clickedThrough;
    }
}

public class SpacerNode : Node
{
    public override int Width { get; }
    public override int Height { get; }

    public SpacerNode(int width = 0, int height = 0)
    {
        Width = width;
        Height = height;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
    }
}

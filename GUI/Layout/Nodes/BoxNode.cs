// #define DEBUG_HOVERS
// #define DEBUG_BOX_BOUNDS

using System.Collections;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public class RefBool
{
    private bool _value;

    public bool Value
    {
        get => _value;
        set => _value = value;
    }

    public static implicit operator bool(RefBool value) => value.Value;
    public static implicit operator RefBool(bool value) => new() { Value = value };
}

public class BoxNode : Node, IEnumerable<Node>
{
    private readonly int? _explicitWidth;
    private readonly int? _explicitHeight;
    private int? _measuredWidth;
    private int? _measuredHeight;

    public bool IgnoreLayout { get; init; }
    public ItemsAlignment HorizontalAlignment { get; init; }
    public ItemsAlignment VerticalAlignment { get; init; }
    public Direction Direction { get; init; }

    public RefBool? IsHovered { get; init; }
    public RefBool? IsHoveredThrough { get; init; }
    public Action? OnClick { get; init; }
    public Action? OnClickThrough { get; init; }

    public override int Width
    {
        get
        {
            if (_explicitWidth.HasValue)
            {
                return _explicitWidth.Value;
            }

            if (_measuredWidth.HasValue == false)
            {
                _measuredWidth = SolidChildren.Any()
                    ? Direction == Direction.Horizontal
                        ? SolidChildren.Sum(child => child.Width) +
                          Style.Spacing * Math.Max(0, SolidChildren.Count() - 1)
                        : SolidChildren.Max(child => child.Width)
                    : 0;
            }

            return _measuredWidth.Value + HorizontalInset;
        }
    }

    public override int Height
    {
        get
        {
            if (_explicitHeight.HasValue)
            {
                return _explicitHeight.Value;
            }

            if (_measuredHeight.HasValue == false)
            {
                _measuredHeight = SolidChildren.Any()
                    ? Direction == Direction.Vertical
                        ? SolidChildren.Sum(child => child.Height) +
                          Style.Spacing * Math.Max(0, SolidChildren.Count() - 1)
                        : SolidChildren.Max(child => child.Height)
                    : 0;
            }

            return _measuredHeight.Value + VerticalInset;
        }
    }

    private int LeftInset => (int)MathF.Ceiling(Style.BorderWidth.Left + Style.Padding.Left);
    private int RightInset => (int)MathF.Ceiling(Style.BorderWidth.Right + Style.Padding.Right);
    private int TopInset => (int)MathF.Ceiling(Style.BorderWidth.Top + Style.Padding.Top);
    private int BottomInset => (int)MathF.Ceiling(Style.BorderWidth.Bottom + Style.Padding.Bottom);
    private int HorizontalInset => LeftInset + RightInset;
    private int VerticalInset => TopInset + BottomInset;

    private BorderRadius BorderRadius => Style.BorderRadius;

    public ICollection<Node> Children { get; init; } = new List<Node>();
    private IEnumerable<Node> SolidChildren => Children.Where(x => x is not BoxNode box || box.IgnoreLayout == false);
    private IEnumerable<Node> EphemeralChildren => Children.Where(x => x is BoxNode { IgnoreLayout: true });

    public BoxNode(int? width = null, int? height = null)
    {
        _explicitWidth = width;
        _explicitHeight = height;
    }

    public void AddNode(Node node)
    {
        Children.Add(node);
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var hovered = Layout.Input.Contains(new Rect(x, y, Width, Height));
        var clicked = hovered && Layout.Input.PointerPressed;
        bool childHovered;
        bool childClicked;

#if DEBUG_HOVERS
        if (hovered)
        {
            Style = Style with { BackgroundColor = Color.FromRgb(255, 0, 0, 0.2f) };
        }
#endif

        DrawBackground(renderer, x, y);
        AddVisualInputRegion(x, y);

        if (Children.Count == 0)
        {
            SetBoxInteractionState(hovered, false, clicked, false);
            DrawDebugBounds(renderer, x, y);
            return;
        }

        var contentX = x + LeftInset;
        var contentY = y + TopInset;
        var contentWidth = Math.Max(0, Width - HorizontalInset);
        var contentHeight = Math.Max(0, Height - VerticalInset);

        if (Direction == Direction.Horizontal)
        {
            (childHovered, childClicked) = DrawHorizontal(renderer, contentX, contentY, contentHeight, contentWidth);
        }
        else
        {
            (childHovered, childClicked) = DrawVertical(renderer, contentX, contentY, contentHeight, contentWidth);
        }

        foreach (var child in EphemeralChildren)
        {
            var childX = contentX + GetAnchorOffset(HorizontalAlignment, contentWidth, child.Width);
            var childY = contentY + GetAnchorOffset(VerticalAlignment, contentHeight, child.Height);
            child.Draw(renderer, childX, childY);
            childHovered |= child.LastHoveredInTree;
            childClicked |= child.LastClickedInTree;
        }

        SetBoxInteractionState(hovered, childHovered, clicked, childClicked);
        DrawDebugBounds(renderer, x, y);
    }

    private void DrawDebugBounds(IRenderApi renderer, int x, int y)
    {
#if DEBUG_BOX_BOUNDS
        renderer.StrokeRect(new Rect(x, y, Width, Height), 1.0f, Color.FromRgb(0, 255, 0));
#endif
    }

    private (bool childHovered, bool childClicked) DrawHorizontal(IRenderApi renderer, int contentX, int contentY,
        int contentHeight, int contentWidth)
    {
        var children = SolidChildren.ToArray();
        if (HorizontalAlignment == ItemsAlignment.Stretch)
        {
            StretchChildWidths(children, contentWidth);
        }

        var childrenWidth = children.Sum(child => child.Width);
        var spacing = GetSpacing(HorizontalAlignment, contentWidth, childrenWidth, children.Length);
        var cursorX = contentX + GetOffset(HorizontalAlignment, contentWidth, childrenWidth, spacing, children.Length);
        var childHovered = false;
        var childClicked = false;

        foreach (var child in children)
        {
            if (child is BoxNode { _explicitHeight: null } boxChild &&
                VerticalAlignment == ItemsAlignment.Stretch)
            {
                boxChild._measuredHeight = Math.Max(0, contentHeight - boxChild.VerticalInset);
            }

            var childY = contentY + GetCrossAxisOffset(VerticalAlignment, contentHeight, child.Height);
            child.Draw(renderer, cursorX, childY);
            childHovered |= child.LastHoveredInTree;
            childClicked |= child.LastClickedInTree;
            cursorX += child.Width + spacing;
        }

        return (childHovered, childClicked);
    }

    private (bool childHovered, bool childClicked) DrawVertical(IRenderApi renderer, int contentX, int contentY,
        int contentHeight, int contentWidth)
    {
        var children = SolidChildren.ToArray();
        if (VerticalAlignment == ItemsAlignment.Stretch)
        {
            StretchChildHeights(children, contentHeight);
        }

        var childrenHeight = children.Sum(child => child.Height);
        var verticalSpacing = GetSpacing(VerticalAlignment, contentHeight, childrenHeight, children.Length);
        var cursorY = contentY + GetOffset(VerticalAlignment, contentHeight, childrenHeight, verticalSpacing, children.Length);
        var childHovered = false;
        var childClicked = false;

        foreach (var child in children)
        {
            if (child is BoxNode { _explicitWidth: null } boxChild &&
                HorizontalAlignment == ItemsAlignment.Stretch)
            {
                boxChild._measuredWidth = Math.Max(0, contentWidth - boxChild.HorizontalInset);
            }

            var childX = contentX + GetCrossAxisOffset(HorizontalAlignment, contentWidth, child.Width);
            child.Draw(renderer, childX, cursorY);
            childHovered |= child.LastHoveredInTree;
            childClicked |= child.LastClickedInTree;
            cursorY += child.Height + verticalSpacing;
        }

        return (childHovered, childClicked);
    }

    private void StretchChildWidths(IReadOnlyList<Node> children, int availableWidth)
    {
        var stretchable = children.OfType<BoxNode>().Where(child => !child._explicitWidth.HasValue).ToArray();
        if (stretchable.Length == 0)
        {
            return;
        }

        var fixedWidth = children.Where(child => child is not BoxNode { _explicitWidth: null }).Sum(child => child.Width);
        var remaining = Math.Max(0, availableWidth - fixedWidth - Style.Spacing * Math.Max(0, children.Count - 1));
        for (var i = 0; i < stretchable.Length; i++)
        {
            var targetWidth = remaining / stretchable.Length + (i < remaining % stretchable.Length ? 1 : 0);
            stretchable[i]._measuredWidth = Math.Max(0, targetWidth - stretchable[i].HorizontalInset);
        }
    }

    private void StretchChildHeights(IReadOnlyList<Node> children, int availableHeight)
    {
        var stretchable = children.OfType<BoxNode>().Where(child => !child._explicitHeight.HasValue).ToArray();
        if (stretchable.Length == 0)
        {
            return;
        }

        var fixedHeight = children.Where(child => child is not BoxNode { _explicitHeight: null }).Sum(child => child.Height);
        var remaining = Math.Max(0, availableHeight - fixedHeight - Style.Spacing * Math.Max(0, children.Count - 1));
        for (var i = 0; i < stretchable.Length; i++)
        {
            var targetHeight = remaining / stretchable.Length + (i < remaining % stretchable.Length ? 1 : 0);
            stretchable[i]._measuredHeight = Math.Max(0, targetHeight - stretchable[i].VerticalInset);
        }
    }

    private void SetBoxInteractionState(bool hovered, bool hoveredThrough, bool clicked, bool clickedThrough)
    {
        IsHovered?.Value = hovered;
        IsHoveredThrough?.Value = hoveredThrough;
        if (clicked)
        {
            OnClick?.Invoke();
        }

        if (clickedThrough)
        {
            OnClickThrough?.Invoke();
        }

        SetInteractionState(hovered, hoveredThrough, clicked, clickedThrough);
    }

    private void DrawBackground(IRenderApi renderer, int x, int y)
    {
        var rect = new Rect(x, y, Width, Height);
        var borderThickness = Style.BorderWidth;
        var cornerRadius = BorderRadius;

        if (Style.BorderColor.HasValue)
        {
            renderer.FillRoundedBorder(rect, cornerRadius, borderThickness, Style.BorderColor.Value);

            if (Style.BackgroundColor.HasValue && borderThickness.Max > 0.0f)
            {
                var inner = rect.Inset(borderThickness);
                renderer.FillRoundedRect(
                    inner,
                    cornerRadius.Inset(borderThickness),
                    Style.BackgroundColor.Value);
            }
            else if (Style.BackgroundColor.HasValue)
            {
                renderer.FillRoundedRect(rect, cornerRadius, Style.BackgroundColor.Value);
            }

            return;
        }

        if (Style.BackgroundColor.HasValue)
        {
            renderer.FillRoundedRect(rect, cornerRadius, Style.BackgroundColor.Value);
        }
    }

    private void AddVisualInputRegion(int x, int y)
    {
        if (Style.BackgroundColor.HasValue || Style.BorderColor.HasValue)
        {
            Layout.AddInputRegion(new Rect(x, y, Width, Height));
        }
    }

    private static Insets NormalizeBorderThickness(Insets width)
    {
        return new Insets(
            MathF.Max(0.0f, width.Top),
            MathF.Max(0.0f, width.Right),
            MathF.Max(0.0f, width.Bottom),
            MathF.Max(0.0f, width.Left));
    }

    private int GetSpacing(ItemsAlignment alignment, int available, int childrenSize, int childrenCount)
    {
        if (alignment != ItemsAlignment.Spread || childrenCount < 2)
        {
            return Style.Spacing;
        }

        var usedSize = childrenSize + Style.Spacing * (childrenCount - 1);
        var extraSpace = Math.Max(0, available - usedSize);
        return Style.Spacing + extraSpace / (childrenCount - 1);
    }

    private int GetOffset(ItemsAlignment alignment, int available, int childrenSize, int spacing, int childrenCount)
    {
        var usedSize = childrenSize + spacing * Math.Max(0, childrenCount - 1);
        var extraSpace = Math.Max(0, available - usedSize);

        return alignment switch
        {
            ItemsAlignment.Center => extraSpace / 2,
            ItemsAlignment.End => extraSpace,
            ItemsAlignment.Spread when childrenCount == 1 => extraSpace / 2,
            _ => 0,
        };
    }

    private static int GetCrossAxisOffset(ItemsAlignment alignment, int available, int childSize)
    {
        var extraSpace = (float)Math.Max(0, available - childSize);

        return (int)(alignment switch
        {
            ItemsAlignment.Center => extraSpace / 2,
            ItemsAlignment.End => extraSpace,
            ItemsAlignment.Spread or ItemsAlignment.Stretch => 0,
            _ => 0,
        });
    }

    private static int GetAnchorOffset(ItemsAlignment alignment, int available, int childSize)
    {
        var extraSpace = available - childSize;
        return alignment switch
        {
            ItemsAlignment.Center or ItemsAlignment.Spread => extraSpace / 2,
            ItemsAlignment.End => extraSpace,
            _ => 0,
        };
    }

    public IEnumerator<Node> GetEnumerator()
    {
        return Children.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public void Add(Node node)
    {
        Children.Add(node);
    }
}

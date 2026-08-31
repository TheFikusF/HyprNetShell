// #define DEBUG_HOVERS
// #define DEBUG_BOX_BOUNDS

using System.Collections;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public class BoxNode : Node, IEnumerable<Node>, IWidthBoundNode, IHeightBoundNode
{
    private readonly int? _explicitWidth;
    private readonly int? _explicitHeight;
    private int? _measuredWidth;
    private int? _measuredHeight;
    private int? _maxWidth;
    private int? _maxHeight;
    private int? _stretchedWidth;
    private int? _stretchedHeight;

    public bool IgnoreLayout { get; init; }
    public int? Top { get; init; }
    public int? Right { get; init; }
    public int? Bottom { get; init; }
    public int? Left { get; init; }
    public ItemsAlignment HorizontalAlignment { get; init; }
    public ItemsAlignment VerticalAlignment { get; init; }
    public Direction Direction { get; init; }

    public Ref<bool>? IsHovered { get; init; }
    public Ref<bool>? IsHoveredThrough { get; init; }
    public Action? OnClick { get; init; }
    public Action? OnClickThrough { get; init; }
    public Action<float>? OnScroll { get; init; }

    public bool AcceptsWidthBound => !_explicitWidth.HasValue;
    public bool AcceptsHeightBound => !_explicitHeight.HasValue;

    public override int Width
    {
        get
        {
            if (_explicitWidth.HasValue)
            {
                return _explicitWidth.Value;
            }

            if (_stretchedWidth.HasValue)
            {
                return _stretchedWidth.Value;
            }

            if (_measuredWidth.HasValue == false)
            {
                Layout.RecordWidthMeasurement();
                _measuredWidth = MeasureChildrenWidth() + HorizontalInset;
            }

            return _maxWidth.HasValue
                ? Math.Min(_measuredWidth.Value, _maxWidth.Value)
                : _measuredWidth.Value;
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

            if (_stretchedHeight.HasValue)
            {
                return _stretchedHeight.Value;
            }


            if (_measuredHeight.HasValue == false)
            {
                Layout.RecordHeightMeasurement();
                PrepareChildWidthBounds();

                _measuredHeight = MeasureChildrenHeight() + VerticalInset;
            }

            return _maxHeight.HasValue
                ? Math.Min(_measuredHeight.Value, _maxHeight.Value)
                : _measuredHeight.Value;
        }
    }

    private int LeftInset => (int)MathF.Ceiling(Style.BorderWidth.Left + Style.Padding.Left);
    private int RightInset => (int)MathF.Ceiling(Style.BorderWidth.Right + Style.Padding.Right);
    private int TopInset => (int)MathF.Ceiling(Style.BorderWidth.Top + Style.Padding.Top);
    private int BottomInset => (int)MathF.Ceiling(Style.BorderWidth.Bottom + Style.Padding.Bottom);
    private int HorizontalInset => LeftInset + RightInset;
    private int VerticalInset => TopInset + BottomInset;

    private BorderRadius BorderRadius => Style.BorderRadius;

    public ICollection<Node> Children { get; init; } = [];

    private static bool ParticipatesInLayout(Node child) => child is not BoxNode { IgnoreLayout: true };

    public BoxNode(int? width = null, int? height = null)
    {
        _explicitWidth = width;
        _explicitHeight = height;
    }

    public BoxNode(Style style, ItemsAlignment? horizontalAlignment = null, ItemsAlignment? verticalAlignment = null)
    {
        Style = style;
        HorizontalAlignment = horizontalAlignment ?? HorizontalAlignment;
        VerticalAlignment = verticalAlignment ?? VerticalAlignment;
    }

    public void SetMaxWidth(int maxWidth, bool stretch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWidth);
        if (!AcceptsWidthBound || _maxWidth == maxWidth && _stretchedWidth == (stretch ? maxWidth : null))
        {
            return;
        }

        _maxWidth = maxWidth;
        _stretchedWidth = stretch ? maxWidth : null;
        _measuredHeight = null;
    }

    public void SetMaxHeight(int maxHeight, bool stretch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxHeight);
        if (!AcceptsHeightBound || _maxHeight == maxHeight && _stretchedHeight == (stretch ? maxHeight : null))
        {
            return;
        }

        _maxHeight = maxHeight;
        _stretchedHeight = stretch ? maxHeight : null;
        _measuredWidth = null;
    }

    public void AddNode(Node node)
    {
        Children.Add(node);
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        Layout.RecordBoxDraw();
        var hovered = Layout.Input.Contains(new Rect(x, y, Width, Height));
        var clicked = hovered && Layout.Input.PointerPressed && !Layout.IsNormalLayerClickBlocked;

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

        var (childHovered, childClicked) = Direction == Direction.Horizontal
            ? DrawHorizontal(renderer, contentX, contentY, contentHeight, contentWidth)
            : DrawVertical(renderer, contentX, contentY, contentHeight, contentWidth);

        foreach (var child in Children)
        {
            if (ParticipatesInLayout(child))
            {
                continue;
            }

            child.Opacity *= Opacity;
            var childX = contentX + GetAbsoluteHorizontalOffset(child, contentWidth);
            var childY = contentY + GetAbsoluteVerticalOffset(child, contentHeight);
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

    private int MeasureChildrenWidth()
    {
        if (Direction == Direction.Horizontal)
        {
            var width = SumChildWidths(out var count);
            return width + Style.Spacing * Math.Max(0, count - 1);
        }

        var maximum = 0;
        foreach (var child in Children)
        {
            if (ParticipatesInLayout(child))
            {
                maximum = Math.Max(maximum, child.Width);
            }
        }

        return maximum;
    }

    private int MeasureChildrenHeight()
    {
        if (Direction == Direction.Vertical)
        {
            var height = SumChildHeights(out var count);
            return height + Style.Spacing * Math.Max(0, count - 1);
        }

        var maximum = 0;
        foreach (var child in Children)
        {
            if (ParticipatesInLayout(child))
            {
                maximum = Math.Max(maximum, child.Height);
            }
        }

        return maximum;
    }

    private int SumChildWidths(out int count)
    {
        count = 0;
        var width = 0;
        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child))
            {
                continue;
            }

            count++;
            width += child.Width;
        }

        return width;
    }

    private int SumChildHeights(out int count)
    {
        count = 0;
        var height = 0;
        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child))
            {
                continue;
            }

            count++;
            height += child.Height;
        }

        return height;
    }

    private (bool childHovered, bool childClicked) DrawHorizontal(IRenderApi renderer, int contentX, int contentY,
        int contentHeight, int contentWidth)
    {
        BoundChildWidths(Children, contentWidth, HorizontalAlignment == ItemsAlignment.Stretch);
        BoundChildHeights(Children, contentHeight, VerticalAlignment == ItemsAlignment.Stretch);
        if (HorizontalAlignment == ItemsAlignment.Stretch)
        {
            StretchChildWidths(contentWidth);
        }

        var childrenWidth = SumChildWidths(out var childCount);
        var spacing = GetSpacing(HorizontalAlignment, contentWidth, childrenWidth, childCount);
        var cursorX = contentX + GetOffset(HorizontalAlignment, contentWidth, childrenWidth, spacing, childCount);
        var childHovered = false;
        var childClicked = false;

        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child))
            {
                continue;
            }

            child.Opacity *= Opacity;
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
        BoundChildWidths(Children, contentWidth, HorizontalAlignment == ItemsAlignment.Stretch);
        BoundChildHeights(Children, contentHeight, VerticalAlignment == ItemsAlignment.Stretch);
        if (VerticalAlignment == ItemsAlignment.Stretch)
        {
            StretchChildHeights(contentHeight);
        }

        var childrenHeight = SumChildHeights(out var childCount);
        var spacing = GetSpacing(VerticalAlignment, contentHeight, childrenHeight, childCount);
        var cursorY = contentY + GetOffset(VerticalAlignment, contentHeight, childrenHeight, spacing, childCount);
        var childHovered = false;
        var childClicked = false;

        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child))
            {
                continue;
            }

            child.Opacity *= Opacity;
            var childX = contentX + GetCrossAxisOffset(HorizontalAlignment, contentWidth, child.Width);
            child.Draw(renderer, childX, cursorY);
            childHovered |= child.LastHoveredInTree;
            childClicked |= child.LastClickedInTree;
            cursorY += child.Height + spacing;
        }

        return (childHovered, childClicked);
    }

    private void StretchChildWidths(int availableWidth)
    {
        var childCount = 0;
        var stretchableCount = 0;
        var fixedWidth = 0;
        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child))
            {
                continue;
            }

            childCount++;
            if (child is IWidthBoundNode { AcceptsWidthBound: true })
            {
                stretchableCount++;
            }
            else
            {
                fixedWidth += child.Width;
            }
        }

        if (stretchableCount == 0)
        {
            return;
        }

        var remaining = Math.Max(0, availableWidth - fixedWidth - Style.Spacing * Math.Max(0, childCount - 1));
        var index = 0;
        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child) || child is not IWidthBoundNode { AcceptsWidthBound: true } stretchable)
            {
                continue;
            }

            var targetWidth = remaining / stretchableCount + (index < remaining % stretchableCount ? 1 : 0);
            stretchable.SetMaxWidth(targetWidth, true);
            index++;
        }
    }

    private void StretchChildHeights(int availableHeight)
    {
        var childCount = 0;
        var stretchableCount = 0;
        var fixedHeight = 0;
        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child))
            {
                continue;
            }

            childCount++;
            if (child is IHeightBoundNode { AcceptsHeightBound: true })
            {
                stretchableCount++;
            }
            else
            {
                fixedHeight += child.Height;
            }
        }

        if (stretchableCount == 0)
        {
            return;
        }

        var remaining = Math.Max(0, availableHeight - fixedHeight - Style.Spacing * Math.Max(0, childCount - 1));
        var index = 0;
        foreach (var child in Children)
        {
            if (!ParticipatesInLayout(child) || child is not IHeightBoundNode { AcceptsHeightBound: true } stretchable)
            {
                continue;
            }

            var targetHeight = remaining / stretchableCount + (index < remaining % stretchableCount ? 1 : 0);
            stretchable.SetMaxHeight(targetHeight, true);
            index++;
        }
    }

    private void PrepareChildWidthBounds()
    {
        var contentWidth = Math.Max(0, Width - HorizontalInset);
        BoundChildWidths(Children, contentWidth, HorizontalAlignment == ItemsAlignment.Stretch);
        if (Direction == Direction.Horizontal && HorizontalAlignment == ItemsAlignment.Stretch)
        {
            StretchChildWidths(contentWidth);
        }
    }

    private static void BoundChildWidths(IEnumerable<Node> children, int maxWidth, bool stretch)
    {
        foreach (var child in children)
        {
            if (ParticipatesInLayout(child) && child is IWidthBoundNode { AcceptsWidthBound: true } bound)
            {
                bound.SetMaxWidth(maxWidth, stretch);
            }
        }
    }

    private static void BoundChildHeights(IEnumerable<Node> children, int maxHeight, bool stretch)
    {
        foreach (var child in children)
        {
            if (ParticipatesInLayout(child) && child is IHeightBoundNode { AcceptsHeightBound: true } bound)
            {
                bound.SetMaxHeight(maxHeight, stretch);
            }
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

        if (hovered && Layout.Input.ScrollDelta != 0.0f)
        {
            OnScroll?.Invoke(Layout.Input.ScrollDelta);
        }

        SetInteractionState(hovered, hoveredThrough, clicked, clickedThrough);
    }

    private void DrawBackground(IRenderApi renderer, int x, int y)
    {
        var rect = new Rect(x, y, Width, Height);
        var borderThickness = Style.BorderWidth;
        var cornerRadius = BorderRadius;

        if (Style.ShadowColor.HasValue && Style.ShadowDistance > 0.0f)
        {
            renderer.FillRoundedShadow(
                rect,
                cornerRadius,
                Style.ShadowColor.Value.PushOpacity(Opacity),
                Style.ShadowDistance);
        }

        if (Style.BorderColor.HasValue)
        {
            renderer.FillRoundedBorder(rect, cornerRadius, borderThickness, Style.BorderColor.Value.PushOpacity(Opacity));

            if (Style.BackgroundColor.HasValue && borderThickness.Max > 0.0f)
            {
                var inner = rect.Inset(borderThickness);
                renderer.FillRoundedRect(
                    inner,
                    cornerRadius.Inset(borderThickness),
                    Style.BackgroundColor.Value.PushOpacity(Opacity));
            }
            else if (Style.BackgroundColor.HasValue)
            {
                renderer.FillRoundedRect(rect, cornerRadius, Style.BackgroundColor.Value.PushOpacity(Opacity));
            }

            return;
        }

        if (Style.BackgroundColor.HasValue)
        {
            renderer.FillRoundedRect(rect, cornerRadius, Style.BackgroundColor.Value.PushOpacity(Opacity));
        }
    }

    private void AddVisualInputRegion(int x, int y)
    {
        if (Style.BackgroundColor.HasValue || Style.BorderColor.HasValue)
        {
            Layout.AddInputRegion(new Rect(x, y, Width, Height));
        }
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

    private static int GetOffset(ItemsAlignment alignment, int available, int childrenSize, int spacing, int childrenCount)
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

    private int GetAbsoluteHorizontalOffset(Node child, int available)
    {
        if (child is BoxNode { Left: { } left })
        {
            return left;
        }

        if (child is BoxNode { Right: { } right })
        {
            return available - child.Width - right;
        }

        return GetAnchorOffset(HorizontalAlignment, available, child.Width);
    }

    private int GetAbsoluteVerticalOffset(Node child, int available)
    {
        if (child is BoxNode { Top: { } top })
        {
            return top;
        }

        if (child is BoxNode { Bottom: { } bottom })
        {
            return available - child.Height - bottom;
        }

        return GetAnchorOffset(VerticalAlignment, available, child.Height);
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

using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

/// <summary>A retained, line-scrolling text viewport. Null dimensions use parent layout bounds.</summary>
public sealed class ScrollableTextNode : Node, IWidthBoundNode, IHeightBoundNode
{
    private const int DEFAULT_WIDTH = 320;
    private const int DEFAULT_HEIGHT = 240;
    private const int SCROLLBAR_WIDTH = 8;
    private const int SCROLLBAR_GAP = 8;
    private const int MINIMUM_THUMB_HEIGHT = 20;
    private const int WHEEL_LINES = 4;

    private readonly int? _width;
    private readonly int? _height;
    private readonly float? _fontSize;
    private readonly Color? _color;

    private string _text;
    private int _resolvedWidth = DEFAULT_WIDTH;
    private int _resolvedHeight = DEFAULT_HEIGHT;
    private TextNode? _wrappedText;
    private int _cachedWidth;
    private float _cachedFontSize;
    private int _firstVisibleLine;
    private bool _dragging;
    private float _dragOffset;

    public override int Width => _width ?? _resolvedWidth;
    public override int Height => _height ?? _resolvedHeight;
    public bool AcceptsWidthBound => !_width.HasValue;
    public bool AcceptsHeightBound => !_height.HasValue;
    public int FirstVisibleLine => _firstVisibleLine;

    public string Text
    {
        get => _text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_text == value)
            {
                return;
            }

            _text = value;
            _wrappedText = null;
            ScrollToStart();
        }
    }

    public ScrollableTextNode(
        string text,
        int? width = null,
        int? height = null,
        float? fontSize = null,
        Color? color = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (width is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (fontSize.HasValue && (!float.IsFinite(fontSize.Value) || fontSize.Value <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        }

        _text = text;
        _width = width;
        _height = height;
        _fontSize = fontSize;
        _color = color;
    }

    public void SetMaxWidth(int maxWidth, bool stretch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxWidth);
        if (AcceptsWidthBound)
        {
            _resolvedWidth = stretch ? maxWidth : Math.Min(DEFAULT_WIDTH, maxWidth);
        }
    }

    public void SetMaxHeight(int maxHeight, bool stretch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxHeight);
        if (AcceptsHeightBound)
        {
            _resolvedHeight = stretch ? maxHeight : Math.Min(DEFAULT_HEIGHT, maxHeight);
        }
    }

    public void ScrollToStart()
    {
        _firstVisibleLine = 0;
        _dragging = false;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var inheritedOpacity = Opacity;
        // Parent boxes multiply retained child opacity each frame, including popup fades.
        Opacity = 1.0f;
        UpdateInteractionState(x, y);
        Layout.AddInputRegion(new Rect(x, y, Width, Height));
        var theme = ThemeManager.Current;
        var fontSize = _fontSize ?? theme.Text.Size;
        var lineHeight = Math.Max(1, (int)MathF.Ceiling(fontSize));
        var left = (int)MathF.Ceiling(Style.Padding.Left);
        var top = (int)MathF.Ceiling(Style.Padding.Top);
        var contentWidth = Math.Max(0, Width - left - (int)MathF.Ceiling(Style.Padding.Right));
        var contentHeight = Math.Max(0, Height - top - (int)MathF.Ceiling(Style.Padding.Bottom));
        var visibleLines = contentHeight / lineHeight;
        // Reserve a stable gutter so overflow does not change the wrapping width.
        var textWidth = Math.Max(0, contentWidth - SCROLLBAR_WIDTH - SCROLLBAR_GAP);
        if (textWidth == 0 || visibleLines == 0)
        {
            _dragging = false;
            return;
        }

        if (_wrappedText is null || _cachedWidth != textWidth || _cachedFontSize != fontSize)
        {
            _wrappedText = new TextNode(Text, fontSize, maxWidth: textWidth, wrapping: TextWrapping.Wrap);
            _cachedWidth = textWidth;
            _cachedFontSize = fontSize;
        }

        var lines = _wrappedText.GetLines(renderer);
        var maximumFirstLine = Math.Max(0, lines.Count - visibleLines);
        _firstVisibleLine = Math.Clamp(_firstVisibleLine, 0, maximumFirstLine);
        var trackX = x + left + contentWidth - SCROLLBAR_WIDTH;
        var trackY = y + top;
        var thumbHeight = Math.Clamp(
            contentHeight * (float)visibleLines / Math.Max(1, lines.Count),
            Math.Min(MINIMUM_THUMB_HEIGHT, contentHeight), contentHeight);
        var travel = contentHeight - thumbHeight;
        var thumbY = trackY + travel * _firstVisibleLine / Math.Max(1, maximumFirstLine);
        var input = Layout.Input;
        var acceptsInput = !Layout.IsLowerLayerClickBlocked;
        if (!input.PointerDown || !acceptsInput || maximumFirstLine == 0)
        {
            _dragging = false;
        }

        if (acceptsInput && LastHovered && float.IsFinite(input.ScrollDelta) && input.ScrollDelta != 0)
        {
            // Wayland supplies axis distances, not wheel-detent counts.
            _firstVisibleLine = Math.Clamp(
                _firstVisibleLine + Math.Sign(input.ScrollDelta) * WHEEL_LINES, 0, maximumFirstLine);
        }

        if (acceptsInput && maximumFirstLine > 0 && input.PointerPressed &&
            input.Contains(new Rect(trackX, trackY, SCROLLBAR_WIDTH, contentHeight)))
        {
            _dragging = true;
            _dragOffset = input.PointerY >= thumbY && input.PointerY <= thumbY + thumbHeight
                ? input.PointerY - thumbY
                : thumbHeight / 2;
        }

        if (_dragging && input.HasPointer && travel > 0)
        {
            var progress = Math.Clamp((input.PointerY - trackY - _dragOffset) / travel, 0, 1);
            _firstVisibleLine = (int)MathF.Round(progress * maximumFirstLine);
        }

        var color = (_color ?? theme.Text.Color).PushOpacity(inheritedOpacity);
        var end = Math.Min(lines.Count, _firstVisibleLine + visibleLines);
        for (var index = _firstVisibleLine; index < end; index++)
        {
            renderer.DrawText(lines[index], x + left,
                trackY + (index - _firstVisibleLine) * lineHeight + (int)(fontSize * 0.8f),
                fontSize, color);
        }

        if (maximumFirstLine > 0)
        {
            var scrollbar = new ScrollbarNode(contentHeight, _firstVisibleLine, lines.Count,
                visibleLines, theme.Panel, theme.Text.MutedColor, SCROLLBAR_WIDTH) {
                Opacity = inheritedOpacity
            };
            scrollbar.Draw(renderer, trackX, trackY);
        }
    }
}

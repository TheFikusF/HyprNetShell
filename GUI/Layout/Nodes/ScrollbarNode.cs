using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class ScrollbarNode : Node
{
    private const int MINIMUM_THUMB_HEIGHT = 20;

    private readonly int _firstItem;
    private readonly int _totalItems;
    private readonly int _visibleItems;
    private readonly Color _trackColor;
    private readonly Color _thumbColor;

    public override int Width { get; }
    public override int Height { get; }

    public ScrollbarNode(
        int height,
        int firstItem,
        int totalItems,
        int visibleItems,
        Color trackColor,
        Color thumbColor,
        int width = 8)
    {
        Width = width;
        Height = Math.Max(0, height);
        _firstItem = Math.Max(0, firstItem);
        _totalItems = Math.Max(0, totalItems);
        _visibleItems = Math.Max(0, visibleItems);
        _trackColor = trackColor;
        _thumbColor = thumbColor;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        SetInteractionState(false, false, false, false);
        if (Width <= 0 || Height <= 0 || _totalItems <= _visibleItems)
        {
            return;
        }

        var radius = Width / 2.0f;
        renderer.FillRoundedRect(new Rect(x, y, Width, Height), radius, _trackColor.PushOpacity(Opacity));

        var thumbHeight = Math.Clamp(
            Height * _visibleItems / Math.Max(1, _totalItems),
            Math.Min(MINIMUM_THUMB_HEIGHT, Height),
            Height);
        var maximumFirstItem = Math.Max(1, _totalItems - _visibleItems);
        var progress = Math.Clamp((float)_firstItem / maximumFirstItem, 0.0f, 1.0f);
        var thumbY = y + (Height - thumbHeight) * progress;
        renderer.FillRoundedRect(
            new Rect(x, thumbY, Width, thumbHeight),
            radius,
            _thumbColor.PushOpacity(Opacity));
    }
}

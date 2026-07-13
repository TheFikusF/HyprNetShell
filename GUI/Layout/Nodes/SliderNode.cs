using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class SliderNode(
    int width,
    int height,
    float value,
    Color trackColor,
    Color fillColor,
    Color thumbColor,
    Action<float> onValueChanged,
    RefBool dragging) : Node
{
    private const float TRACK_HEIGHT = 6.0f;
    private const float THUMB_SIZE = 14.0f;
    
    public override int Width { get; } = width;
    public override int Height { get; } = height;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var bounds = new Rect(x, y, Width, Height);
        var hovered = Layout.Input.Contains(bounds);
        if (hovered && Layout.Input.PointerPressed)
        {
            dragging.Value = true;
        }
        else if (!Layout.Input.PointerDown)
        {
            dragging.Value = false;
        }

        if (dragging.Value && Layout.Input.HasPointer)
        {
            onValueChanged(Math.Clamp((Layout.Input.PointerX - x) / Math.Max(1.0f, Width), 0.0f, 1.0f));
        }
        
        var normalizedValue = Math.Clamp(value, 0.0f, 1.0f);
        var track = new Rect(x, y + (Height - TRACK_HEIGHT) / 2.0f, Width, TRACK_HEIGHT);
        renderer.FillRoundedRect(track, TRACK_HEIGHT / 2.0f, trackColor);
        if (normalizedValue > 0.0f)
        {
            renderer.FillRoundedRect(track with { Width = track.Width * normalizedValue }, TRACK_HEIGHT / 2.0f, fillColor);
        }

        var thumbX = x + normalizedValue * Width;
        renderer.FillRoundedRect(
            new Rect(thumbX - THUMB_SIZE / 2.0f, y + (Height - THUMB_SIZE) / 2.0f, THUMB_SIZE, THUMB_SIZE),
            THUMB_SIZE / 2.0f,
            thumbColor);

        Layout.AddInputRegion(bounds);
        SetInteractionState(hovered, false, hovered && Layout.Input.PointerPressed, false);
    }
}

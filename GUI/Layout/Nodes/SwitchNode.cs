using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class SwitchNode(bool on, RefFloat animation) : Node
{
    private const int SWITCH_WIDTH = 44;
    private const int SWITCH_HEIGHT = 28;
    private const float TRACK_WIDTH = 36.0f;
    private const float TRACK_HEIGHT = 16.0f;
    private const float KNOB_SIZE = 20.0f;
    private const float ANIMATION_DECAY = 18.0f;
    private const float DELTA_TIME = 1.0f / 30.0f;

    public Color OffTrackColor { get; init; } = Color.FromRgb(96, 96, 96);
    public Color OnTrackColor { get; init; } = Color.Orange;
    public Color KnobColor { get; init; } = Color.White;

    public override int Width => SWITCH_WIDTH;
    public override int Height => SWITCH_HEIGHT;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var target = on ? 1.0f : 0.0f;
        animation.Value = PrimitivesMath.LerpSmooth(
            Math.Clamp(animation.Value, 0.0f, 1.0f),
            target,
            ANIMATION_DECAY,
            DELTA_TIME);

        if (MathF.Abs(animation.Value - target) < 0.001f)
        {
            animation.Value = target;
        }

        var progress = animation.Value;
        var trackX = x + (Width - TRACK_WIDTH) / 2.0f;
        var trackY = y + (Height - TRACK_HEIGHT) / 2.0f;
        renderer.FillRoundedRect(
            new Rect(trackX, trackY, TRACK_WIDTH, TRACK_HEIGHT),
            TRACK_HEIGHT / 2.0f,
            Color.Lerp(OffTrackColor, OnTrackColor, progress).PushOpacity(Opacity));

        var knobTravel = TRACK_WIDTH - KNOB_SIZE;
        var knobX = trackX + knobTravel * progress;
        var knobY = y + (Height - KNOB_SIZE) / 2.0f;
        renderer.FillRoundedRect(
            new Rect(knobX, knobY, KNOB_SIZE, KNOB_SIZE),
            KNOB_SIZE / 2.0f,
            KnobColor.PushOpacity(Opacity));

        UpdateInteractionState(x, y);
    }
}

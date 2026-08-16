using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class RadioButtonNode(bool selected) : Node
{
    private const int SIZE = 24;
    private const float OUTER_SIZE = 18.0f;
    private const float BORDER_WIDTH = 2.0f;
    private const float DOT_SIZE = 10.0f;

    public Color SelectedColor { get; init; } = Color.Orange;
    public Color UnselectedColor { get; init; } = Color.FromRgb(160, 160, 160);
    public Color BackgroundColor { get; init; } = Color.FromRgb(31, 35, 44);

    public override int Width => SIZE;
    public override int Height => SIZE;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var outerX = x + (Width - OUTER_SIZE) / 2.0f;
        var outerY = y + (Height - OUTER_SIZE) / 2.0f;
        var ringColor = selected ? SelectedColor : UnselectedColor;

        renderer.FillRoundedRect(
            new Rect(outerX, outerY, OUTER_SIZE, OUTER_SIZE),
            OUTER_SIZE / 2.0f,
            ringColor.PushOpacity(Opacity));
        renderer.FillRoundedRect(
            new Rect(
                outerX + BORDER_WIDTH,
                outerY + BORDER_WIDTH,
                OUTER_SIZE - BORDER_WIDTH * 2.0f,
                OUTER_SIZE - BORDER_WIDTH * 2.0f),
            (OUTER_SIZE - BORDER_WIDTH * 2.0f) / 2.0f,
            BackgroundColor.PushOpacity(Opacity));

        if (selected)
        {
            renderer.FillRoundedRect(
                new Rect(
                    x + (Width - DOT_SIZE) / 2.0f,
                    y + (Height - DOT_SIZE) / 2.0f,
                    DOT_SIZE,
                    DOT_SIZE),
                DOT_SIZE / 2.0f,
                SelectedColor.PushOpacity(Opacity));
        }

        UpdateInteractionState(x, y);
    }
}

using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class CheckboxNode(bool selected, SvgAsset checkIcon) : Node
{
    private const int Size = 24;
    private const float BoxSize = 18.0f;
    private const float BorderWidth = 2.0f;

    public Color SelectedColor { get; init; } = Color.Orange;
    public Color UnselectedColor { get; init; } = Color.FromRgb(160, 160, 160);
    public Color BackgroundColor { get; init; } = Color.FromRgb(31, 35, 44);
    public Color CheckColor { get; init; } = Color.White;

    public override int Width => Size;
    public override int Height => Size;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var boxX = x + (Width - BoxSize) / 2.0f;
        var boxY = y + (Height - BoxSize) / 2.0f;
        var borderColor = selected ? SelectedColor : UnselectedColor;

        renderer.FillRoundedRect(
            new Rect(boxX, boxY, BoxSize, BoxSize),
            4.0f,
            borderColor.PushOpacity(Opacity));

        if (selected)
        {
            renderer.DrawImage(
                checkIcon,
                new Rect(x + 5.0f, y + 5.0f, 14.0f, 14.0f),
                CheckColor,
                opacity: Opacity);
        }
        else
        {
            renderer.FillRoundedRect(
                new Rect(
                    boxX + BorderWidth,
                    boxY + BorderWidth,
                    BoxSize - BorderWidth * 2.0f,
                    BoxSize - BorderWidth * 2.0f),
                2.0f,
                BackgroundColor.PushOpacity(Opacity));
        }

        UpdateInteractionState(x, y);
    }
}

using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public class ImageNode : Node
{
    public override int Width { get; }
    public override int Height { get; }
    private string ImagePath { get; }

    public ImageNode(string imagePath, int width, int height)
    {
        ImagePath = imagePath;
        Width = width;
        Height = height;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
        Layout.AddInputRegion(new Rect(x, y, Width, Height));
        renderer.DrawImage(ImagePath, new Rect(x, y, Width, Height));
    }
}

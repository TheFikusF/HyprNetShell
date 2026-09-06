using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class LayeredNode : BoxNode
{
    private readonly Func<Node> _createContent;
    private readonly RenderLayer _layer;
    private readonly int _offsetX;
    private readonly int _offsetY;

    public LayeredNode(Func<Node> createContent, RenderLayer layer, int offsetX = 0, int offsetY = 0)
    {
        _createContent = createContent;
        _layer = layer;
        _offsetX = offsetX;
        _offsetY = offsetY;
        IgnoreLayout = true;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var content = _createContent();
        var contentX = x + _offsetX;
        var contentY = y + _offsetY;
        var rect = new Rect(contentX, contentY, content.Width, content.Height);

        Layout.RegisterLayerInputRegion(_layer, rect);
        Layout.DrawOnLayer(_layer, layerRenderer =>
        {
            content.Opacity *= Opacity;
            content.Draw(layerRenderer, contentX, contentY);
        });
        SetInteractionState(false, false, false, false);
    }
}

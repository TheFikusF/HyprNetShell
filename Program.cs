using HyprNetShell;
using HyprNetShell.Core.Bar;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;

const int BAR_HEIGHT = 52;

try
{
    using var layer = new HyprLayer(BAR_HEIGHT);
    using var renderer = new Renderer(HyprLayer.GetProcAddress);
    using var bar = new StatusBar(renderer, BAR_HEIGHT);

    while (layer.Update())
    {
        renderer.BeginFrame(layer.LayerSize.width, layer.LayerSize.height);
        Layout.Input = layer.Input;
        Layout.BeginInputRegionFrame();
        bar.Draw();
        layer.SetInputRegions(Layout.GetInputRegions());
        renderer.EndFrame();
        layer.Swap();
    }

    return layer.ReturnCode;
}
catch (Exception e)
{
    Console.Error.WriteLine(e);
    return 1;
}

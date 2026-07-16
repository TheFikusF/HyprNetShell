using HyprNetShell;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;

const int BAR_HEIGHT = 52;

AppLogger.Initialize();
try
{
    using var layer = new HyprLayer(BAR_HEIGHT);
    using var renderer = new Renderer(HyprLayer.GetProcAddress);
    using var bar = new StatusBar(renderer, BAR_HEIGHT);

    var keyboardInteractive = false;

    while (layer.Update())
    {
        bar.HandleMainDialogInput(layer.PressedKey, layer.TextInput, layer.Input.ScrollDelta);
        if (keyboardInteractive != bar.IsMainDialogOpen)
        {
            keyboardInteractive = bar.IsMainDialogOpen;
            layer.SetKeyboardInteractivity(keyboardInteractive);
        }

        renderer.BeginFrame(layer.LayerSize.width, layer.LayerSize.height);
        Layout.Input = layer.Input;
        Layout.BeginInputRegionFrame();
        bar.Draw();
        layer.SetInputRegions(bar.IsMainDialogOpen
            ? [new(0, 0, layer.LayerSize.width, layer.LayerSize.height)]
            : Layout.GetInputRegions());
        renderer.EndFrame();
        layer.Swap();
    }

    return layer.ReturnCode;
}
catch (Exception e)
{
    AppLogger.Error("Application", "Fatal error", e);
    return 1;
}
finally
{
    AppLogger.Shutdown();
}

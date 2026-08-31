using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

internal sealed class WeatherTemperatureRangeNode(
    double minimum,
    double maximum,
    double overallMinimum,
    double overallMaximum,
    Theme theme) : Node
{
    public override int Width => 72;
    public override int Height => 8;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
        var track = new Rect(x, y, Width, Height);
        renderer.FillRoundedRect(track, Height / 2f, Color.Lighten(theme.Panel, 0.15f));

        var span = Math.Max(1, overallMaximum - overallMinimum);
        var start = (float)((minimum - overallMinimum) / span * Width);
        var end = (float)((maximum - overallMinimum) / span * Width);
        var range = new Rect(x + start, y, Math.Max(3, end - start), Height);
        var temperature = (float)(((minimum + maximum) / 2 - overallMinimum) / span);
        var color = Color.Lerp(Color.Blue, Color.Lerp(Color.Orange, Color.Yellow, 0.3f), temperature);
        renderer.FillRoundedRect(range, Height / 2f, color);
    }
}

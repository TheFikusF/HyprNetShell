using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class SystemHistoryGraphNode(
    int width,
    int height,
    IReadOnlyList<float> primaryHistory,
    IReadOnlyList<float>? secondaryHistory,
    float maximum,
    Color primaryColor,
    Color secondaryColor,
    Color backgroundColor,
    Color gridColor) : Node
{
    private const float HEADER_HEIGHT = 22.0f;
    private const float PADDING = 8.0f;

    public override int Width { get; } = width;
    public override int Height { get; } = height;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        var bounds = new Rect(x, y, Width, Height);
        renderer.FillRoundedRect(bounds, 8.0f, backgroundColor.PushOpacity(Opacity));
        renderer.StrokeRect(bounds, 1.0f, gridColor.PushOpacity(Opacity));

        var plot = new Rect(
            x + PADDING,
            y + HEADER_HEIGHT,
            Width - PADDING * 2.0f,
            Height - HEADER_HEIGHT - PADDING);
        DrawGrid(renderer, plot);
        DrawHistory(renderer, plot, primaryHistory, primaryColor, secondaryHistory is not null, false);
        if (secondaryHistory is not null)
        {
            DrawHistory(renderer, plot, secondaryHistory, secondaryColor, true, true);
        }

        UpdateInteractionState(x, y);
    }

    private void DrawGrid(IRenderApi renderer, Rect plot)
    {
        if (secondaryHistory is null)
        {
            for (var row = 1; row < 4; row++)
            {
                var lineY = plot.Y + plot.Height * row / 4.0f;
                renderer.FillRect(new Rect(plot.X, lineY, plot.Width, 1.0f), gridColor.PushOpacity(Opacity));
            }
        }
        else
        {
            renderer.FillRect(
                new Rect(plot.X, plot.Y + plot.Height / 4.0f, plot.Width, 1.0f),
                gridColor.PushOpacity(Opacity));
            renderer.FillRect(
                new Rect(plot.X, plot.Y + plot.Height * 3.0f / 4.0f, plot.Width, 1.0f),
                gridColor.PushOpacity(Opacity));
            var middle = plot.Y + plot.Height / 2.0f;
            renderer.FillRect(new Rect(plot.X, middle, plot.Width, 1.0f), gridColor.PushOpacity(Opacity * 1.6f));
        }

        for (var column = 1; column < 8; column++)
        {
            var lineX = plot.X + plot.Width * column / 8.0f;
            renderer.FillRect(new Rect(lineX, plot.Y, 1.0f, plot.Height), gridColor.PushOpacity(Opacity * 0.7f));
        }
    }

    private void DrawHistory(
        IRenderApi renderer,
        Rect plot,
        IReadOnlyList<float> history,
        Color color,
        bool doubleSided,
        bool lowerHalf)
    {
        if (history.Count == 0)
        {
            return;
        }

        var slotWidth = plot.Width / Math.Max(1, history.Count);
        var barWidth = MathF.Max(1.0f, slotWidth + 0.35f);
        var availableHeight = doubleSided ? plot.Height / 2.0f - 1.0f : plot.Height;
        var baseline = lowerHalf ? plot.Y + plot.Height / 2.0f + 1.0f : plot.Y + (doubleSided ? plot.Height / 2.0f : plot.Height);
        for (var index = 0; index < history.Count; index++)
        {
            var normalized = Math.Clamp(history[index] / Math.Max(1.0f, maximum), 0.0f, 1.0f);
            var barHeight = normalized * availableHeight;
            if (barHeight < 0.5f)
            {
                continue;
            }

            var barX = plot.X + index * slotWidth;
            var barY = lowerHalf ? baseline : baseline - barHeight;
            var ageOpacity = 0.12f + 0.88f * (index + 1.0f) / history.Count;
            renderer.FillRect(
                new Rect(barX, barY, barWidth, barHeight),
                color.PushOpacity(Opacity * ageOpacity));
        }
    }
}

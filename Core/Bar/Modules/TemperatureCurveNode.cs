using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class TemperatureCurveDragState
{
    public int PointIndex { get; set; } = -1;
}

internal sealed class TemperatureCurveNode(
    IReadOnlyList<TemperatureCurvePoint> points,
    Color gridColor,
    Color curveColor,
    Color pointColor,
    Action<int, float, int> onPointChanged,
    TemperatureCurveDragState dragState) : Node
{
    private Color _gridColor = gridColor;
    private Color _curveColor = curveColor;
    private Color _pointColor = pointColor;
    
    private const float LEFT = 42.0f;
    private const float RIGHT = 8.0f;
    private const float TOP = 10.0f;
    private const float BOTTOM = 22.0f;

    public override int Width { get; } = 340;
    public override int Height { get; } = 150;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        _gridColor = _gridColor.PushOpacity(Opacity);
        _curveColor = _curveColor.PushOpacity(Opacity);
        _pointColor = _pointColor.PushOpacity(Opacity);
        var bounds = new Rect(x, y, Width, Height);
        var plot = new Rect(x + LEFT, y + TOP, Width - LEFT - RIGHT, Height - TOP - BOTTOM);
        DrawGrid(renderer, plot);
        DrawCurve(renderer, plot);
        HandleInput(plot);

        var hovered = Layout.Input.HasPointer && bounds.Contains(Layout.Input.PointerX, Layout.Input.PointerY);
        SetInteractionState(hovered, false, hovered && Layout.Input.PointerPressed, false);
    }

    private void DrawGrid(IRenderApi renderer, Rect plot)
    {
        void DrawTimeLine(float hour, Color color)
        {
            var px = plot.X + plot.Width * hour / 24.0f;
            renderer.FillRect(new Rect(px, plot.Y, 1, plot.Height), color);
            renderer.DrawText(hour.ToString("00"), px - 7, plot.Y + plot.Height + 15, 10, color);
        }

        void DrawTemperatureLine(int temperature, Color color)
        {
            var py = TemperatureToY(plot, temperature);
            renderer.FillRect(new Rect(plot.X, py, plot.Width, 1), color);
            renderer.DrawText($"{temperature / 1000.0f:0.#}k", plot.X - 34, py + 5, 10, color);
        }

        for (var hour = 0; hour <= 24; hour += 6)
        {
            DrawTimeLine(hour, _gridColor);
        }

        foreach (var temperature in new[]
                 {
                     TemperatureCurveMath.MAXIMUM_TEMPERATURE,
                     (TemperatureCurveMath.MAXIMUM_TEMPERATURE + TemperatureCurveMath.MINIMUM_TEMPERATURE) / 2,
                     TemperatureCurveMath.MINIMUM_TEMPERATURE,
                 })
        {
            DrawTemperatureLine(temperature, _gridColor);
        }

        if (dragState.PointIndex > -1)
        {
            var point = points[dragState.PointIndex];
            DrawTimeLine(point.Hour, _pointColor);
            DrawTemperatureLine(point.TemperatureKelvin, _pointColor);
        }

        var now = DateTime.Now;
        DrawTimeLine(now.Hour + now.Minute / 60.0f, Color.Orange with { A = 0.55f });
    }

    private void DrawCurve(IRenderApi renderer, Rect plot)
    {
        const int SAMPLES = 145;
        for (var i = 0; i < SAMPLES; i++)
        {
            var hour = 24.0f * i / (SAMPLES - 1);
            var temperature = TemperatureCurveMath.Evaluate(points, hour);
            var px = plot.X + plot.Width * hour / 24.0f;
            var py = TemperatureToY(plot, temperature);
            renderer.FillRoundedRect(new Rect(px - 1.5f, py - 1.5f, 3, 3), 1.5f, _curveColor);
        }

        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            var px = plot.X + plot.Width * point.Hour / 24.0f;
            var py = TemperatureToY(plot, point.TemperatureKelvin);
            renderer.FillRoundedRect(new Rect(px - 5, py - 5, 10, 10), 5, _pointColor);
        }
    }

    private void HandleInput(Rect plot)
    {
        if (!Layout.Input.PointerDown)
        {
            dragState.PointIndex = -1;
            return;
        }

        if (Layout.Input.PointerPressed && plot.Contains(Layout.Input.PointerX, Layout.Input.PointerY))
        {
            dragState.PointIndex = FindClosestPoint(plot, Layout.Input.PointerX, Layout.Input.PointerY);
        }

        if (dragState.PointIndex < 0)
        {
            return;
        }

        var hour = Math.Clamp((Layout.Input.PointerX - plot.X) / plot.Width * 24.0f, 0.0f, 24.0f);
        var normalizedY = Math.Clamp((Layout.Input.PointerY - plot.Y) / plot.Height, 0.0f, 1.0f);
        var temperature = (int)MathF.Round(
            TemperatureCurveMath.MAXIMUM_TEMPERATURE -
            normalizedY * (TemperatureCurveMath.MAXIMUM_TEMPERATURE - TemperatureCurveMath.MINIMUM_TEMPERATURE));
        onPointChanged(dragState.PointIndex, hour, temperature);
    }

    private int FindClosestPoint(Rect plot, float pointerX, float pointerY)
    {
        var closest = -1;
        var closestDistance = float.MaxValue;
        for (var i = 0; i < points.Count; i++)
        {
            var px = plot.X + plot.Width * points[i].Hour / 24.0f;
            var py = TemperatureToY(plot, points[i].TemperatureKelvin);
            var distance = (px - pointerX) * (px - pointerX) + (py - pointerY) * (py - pointerY);
            if (distance < closestDistance)
            {
                closest = i;
                closestDistance = distance;
            }
        }

        return closest;
    }

    private static float TemperatureToY(Rect plot, int temperature) =>
        plot.Y + plot.Height *
        (TemperatureCurveMath.MAXIMUM_TEMPERATURE - temperature) /
        (TemperatureCurveMath.MAXIMUM_TEMPERATURE - TemperatureCurveMath.MINIMUM_TEMPERATURE);
}
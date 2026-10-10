using HyprNetShell.Rendering.ShaderModels;
using System.Numerics;

using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering.Renderers;

public sealed unsafe partial class Renderer
{
    private const int MAX_GRADIENT_STOPS = 64;
    private const float MAX_SHADOW_DISTANCE = 256.0f;
    private const int ROUNDED_MODE_SOLID = 0;
    private const int ROUNDED_MODE_BORDER = 1;
    private const int ROUNDED_MODE_GRADIENT = 2;
    private const int ROUNDED_MODE_SHADOW = 3;

    private readonly RoundedShader _roundedProgramShader;
    private readonly RoundedGeometryBatch _roundedGeometry;

    public void FillRoundedRect(Rect rect, float radius, Color color)
        => FillRoundedRect(rect, new BorderRadius(radius), color);

    public void FillRoundedRect(Rect rect, BorderRadius radius, Color color)
    {
        RecordRoundedRect();
        DrawRoundedRect(rect.X, rect.Y, rect.Width, rect.Height, radius, color);
    }

    public void FillRoundedShadow(Rect rect, BorderRadius radius, Color color, float distance)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f || color.A <= 0.0f ||
            !float.IsFinite(distance) || distance <= 0.0f)
        {
            return;
        }

        RecordShadow();
        distance = MathF.Min(distance, MAX_SHADOW_DISTANCE);
        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        DrawRoundedShape(rect, radius, color, ROUNDED_MODE_SHADOW, default, default, null, distance);
    }

    public void FillRoundedBorder(Rect rect, BorderRadius radius, Insets thickness, Color color)
    {
        RecordRoundedBorder();
        DrawRoundedBorder(rect, radius, thickness, color);
    }

    public void FillRoundedRectGradient(
        Rect rect,
        BorderRadius radius,
        Gradient gradient,
        GradientDirection direction,
        float offset = 0.0f)
        => DrawRoundedGradient(rect, radius, gradient, direction, offset);

    private void DrawRoundedRect(float x, float y, float width, float height, BorderRadius radius, Color color)
    {
        if (width <= 0.0f || height <= 0.0f)
        {
            return;
        }

        radius = ClampCornerRadius(radius, width, height);
        DrawRoundedShape(new Rect(x, y, width, height), radius, color, ROUNDED_MODE_SOLID);
    }

    private void DrawRoundedBorder(Rect rect, BorderRadius radius, Insets thickness, Color color)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f || thickness.Max <= 0.0f)
        {
            return;
        }

        thickness = new Insets(
            MathF.Max(0.0f, thickness.Top),
            MathF.Max(0.0f, thickness.Right),
            MathF.Max(0.0f, thickness.Bottom),
            MathF.Max(0.0f, thickness.Left));

        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        var innerRect = rect.Inset(thickness);
        if (innerRect.Width <= 0.0f || innerRect.Height <= 0.0f)
        {
            DrawRoundedRect(rect.X, rect.Y, rect.Width, rect.Height, radius, color);
            return;
        }

        var innerRadius = ClampCornerRadius(radius.Inset(thickness), innerRect.Width, innerRect.Height);
        DrawRoundedShape(rect, radius, color, ROUNDED_MODE_BORDER, thickness, innerRadius);
    }

    private void DrawRoundedGradient(
        Rect rect,
        BorderRadius radius,
        Gradient gradient,
        GradientDirection direction,
        float offset)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        if (gradient.Stops.Count > MAX_GRADIENT_STOPS)
        {
            throw new ArgumentException(
                $"Rounded gradients support at most {MAX_GRADIENT_STOPS} stops.",
                nameof(gradient));
        }

        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        offset -= MathF.Floor(offset);
        DrawRoundedShape(
            rect,
            radius,
            Color.White,
            ROUNDED_MODE_GRADIENT,
            gradient: gradient,
            gradientDirection: direction,
            gradientOffset: offset);
    }

    private void DrawRoundedShape(
        Rect rect,
        BorderRadius radius,
        Color color,
        int mode,
        Insets thickness = default,
        BorderRadius innerRadius = default,
        Gradient? gradient = null,
        float shadowDistance = 0.0f,
        GradientDirection gradientDirection = GradientDirection.Horizontal,
        float gradientOffset = 0.0f)
    {
        FlushColoredGeometry();
        FlushTextureGeometry();
        if (gradient is not null)
        {
            FlushRoundedGeometry();
        }

        var padding = mode == ROUNDED_MODE_SHADOW ? MathF.Ceiling(shadowDistance) + 1.0f : 0.0f;
        ReadOnlySpan<float> instance =
        [
            rect.X, rect.Y, rect.Width, rect.Height,
            radius.TopLeft, radius.TopRight, radius.BottomRight, radius.BottomLeft,
            color.R, color.G, color.B, color.A,
            thickness.Top, thickness.Right, thickness.Bottom, thickness.Left,
            innerRadius.TopLeft, innerRadius.TopRight, innerRadius.BottomRight, innerRadius.BottomLeft,
            mode, MathF.Max(shadowDistance, 0.0001f), padding, 0,
        ];
        _roundedGeometry.Append(instance);
        RecordColoredDraw(6);
        if (gradient is null)
        {
            return;
        }

        _roundedProgramShader.UploadGradient(gradient, gradientDirection, gradientOffset);

        FlushRoundedGeometry();
    }

    private void FlushRoundedGeometry()
    {
        if (!_roundedGeometry.HasPendingGeometry)
        {
            return;
        }

        _roundedProgramShader.Bind();
        _roundedProgramShader.Viewport = new Vector2(Width, Height);
        var bytes = _roundedGeometry.Upload();
        RecordBufferUpload(bytes);
        _roundedGeometry.Draw();
    }

    private static BorderRadius ClampCornerRadius(BorderRadius radius, float width, float height)
    {
        radius = new BorderRadius(
            MathF.Max(0.0f, radius.TopLeft),
            MathF.Max(0.0f, radius.TopRight),
            MathF.Max(0.0f, radius.BottomRight),
            MathF.Max(0.0f, radius.BottomLeft));

        var scale = 1.0f;
        scale = ClampRadiusScale(scale, width, radius.TopLeft + radius.TopRight);
        scale = ClampRadiusScale(scale, width, radius.BottomLeft + radius.BottomRight);
        scale = ClampRadiusScale(scale, height, radius.TopLeft + radius.BottomLeft);
        scale = ClampRadiusScale(scale, height, radius.TopRight + radius.BottomRight);

        return scale >= 1.0f
            ? radius
            : new BorderRadius(
                radius.TopLeft * scale,
                radius.TopRight * scale,
                radius.BottomRight * scale,
                radius.BottomLeft * scale);
    }

    private static float ClampRadiusScale(float scale, float available, float used)
    {
        return used <= 0.0f ? scale : MathF.Min(scale, available / used);
    }
}

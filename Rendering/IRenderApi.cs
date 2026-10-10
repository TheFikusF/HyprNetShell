using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering;

public interface IRenderApi
{
    int Width
    {
        get;
    }

    int Height
    {
        get;
    }

    float MeasureText(string text, float fontSize);

    void FillRect(Rect rect, Color color);
    void FillRoundedRect(Rect rect, float radius, Color color);
    void FillRoundedRect(Rect rect, BorderRadius radius, Color color);
    void FillRoundedShadow(Rect rect, BorderRadius radius, Color color, float distance);
    void FillRoundedBorder(Rect rect, BorderRadius radius, Insets thickness, Color color);
    void FillRoundedRectGradient(
        Rect rect,
        BorderRadius radius,
        Gradient gradient,
        GradientDirection direction,
        float offset = 0.0f);
    void StrokeRect(Rect rect, float thickness, Color color);
    void DrawImage(
        string imagePath,
        Rect rect,
        Color multiplicativeColor,
        bool loadAsync = false,
        float rotationRadians = 0);
    /// <summary>Draws an alpha-mask Gaussian shadow; blurRadius is the three-sigma support in screen pixels.</summary>
    void DrawImageShadow(string imagePath, Rect rect, Color shadowColor, float blurRadius,
        float offsetX = 0, float offsetY = 2, bool loadAsync = false);
    void DrawImageShadow(SvgAsset asset, Rect rect, Color shadowColor, float blurRadius,
        float offsetX = 0, float offsetY = 2);
    void DrawImage(RawImageData image, Rect rect, Color multiplicativeColor, float rotationRadians = 0);
    void DrawRoundedImage(RawImageData image, Rect rect, float radius, Color color);
    /// <summary>Draws a crystal disc with an outer glow. Time is in seconds; opacity is clamped to 0..1.</summary>
    void DrawCrystalSphere(Rect sphere, float time, float opacity);
    /// <summary>Warps the full image from an absolute source-plane rectangle into a square sphere. Null draws a placeholder.</summary>
    void DrawSphericalImage(RawImageData? image, Rect sourceRect, Rect sphere, Color color, float emphasis, Point rotation = default, float warpStrength = SphereProjection.THUMBNAIL_WARP_STRENGTH);
    void DrawImage(EncodedImageData image, Rect rect, Color multiplicativeColor, float rotationRadians = 0);
    void DrawImage(
        SvgAsset asset,
        Rect rect,
        Color? color,
        float rotationRadians = 0,
        float opacity = 1);
    void DrawText(string text, float x, float y, float fontSize, Color color, float charDistance = 0);
}

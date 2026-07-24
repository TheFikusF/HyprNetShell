using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering;

public interface IRenderApi
{
    int Width { get; }
    int Height { get; }

    float MeasureText(string text, float fontSize);

    void FillRect(Rect rect, Color color);
    void FillRoundedRect(Rect rect, float radius, Color color);
    void FillRoundedRect(Rect rect, BorderRadius radius, Color color);
    void FillRoundedBorder(Rect rect, BorderRadius radius, Insets thickness, Color color);
    void FillRoundedRectHorizontalGradient(Rect rect, BorderRadius radius, Color left, Color right, float offset);
    void StrokeRect(Rect rect, float thickness, Color color);
    void DrawImage(
        string imagePath,
        Rect rect,
        Color multiplicativeColor,
        bool loadAsync = false,
        float rotationRadians = 0);
    void DrawImage(RawImageData image, Rect rect, Color multiplicativeColor, float rotationRadians = 0);
    void DrawImage(EncodedImageData image, Rect rect, Color multiplicativeColor, float rotationRadians = 0);
    void DrawImage(SvgAsset asset, Rect rect, Color color, float rotationRadians = 0);
    void DrawText(string text, float x, float y, float fontSize, Color color, float charDistance = 0);
}

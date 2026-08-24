using SkiaSharp;

namespace HyprNetShell.Rendering;

public static class ImageEncoding
{
    public static EncodedImageData EncodePng(RawImageData source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Width <= 0 || source.Height <= 0 || (long)source.Width * source.Height * 4 != source.RgbaPixels.Length)
        {
            throw new ArgumentException("Invalid RGBA image dimensions or pixel length.", nameof(source));
        }

        var info = new SKImageInfo(
            source.Width,
            source.Height,
            SKColorType.Rgba8888,
            SKAlphaType.Unpremul);

        using var image = SKImage.FromPixelCopy(info, source.RgbaPixels.Span, info.RowBytes)
            ?? throw new InvalidOperationException("Skia could not create the image.");

        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Skia could not encode the image as PNG.");

        return new EncodedImageData("image/png", encoded.ToArray());
    }
}

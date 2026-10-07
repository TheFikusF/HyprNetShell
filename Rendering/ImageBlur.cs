using System.Runtime.InteropServices;
using SkiaSharp;

namespace HyprNetShell.Rendering;

public static class ImageBlur
{
    /// <summary>Decodes and blurs a file on the calling thread; callers should use a background task.</summary>
    public static RawImageData BlurFile(string path, string cacheKey, long revision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException($"Unsupported image file: {path}");
        var source = codec.Info;
        var scale = Math.Min(1d, 768d / Math.Max(source.Width, source.Height));
        var sampled = codec.GetScaledDimensions((float)scale);
        var decodeInfo = new SKImageInfo(sampled.Width, sampled.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(decodeInfo);
        var result = codec.GetPixels(decodeInfo, decoded.GetPixels());
        if (result != SKCodecResult.Success)
        {
            throw new InvalidDataException($"Image decode failed ({result}): {path}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var resized = new SKBitmap(info);
        using (var canvas = new SKCanvas(resized))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(decoded, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        }
        using var blurred = new SKBitmap(info);
        using var filter = SKImageFilter.CreateBlur(18, 18, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = filter };
        using (var canvas = new SKCanvas(blurred))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(resized, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        cancellationToken.ThrowIfCancellationRequested();
        using var rgba = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var blurredImage = SKImage.FromBitmap(blurred);
        if (!blurredImage.ReadPixels(rgba.Info, rgba.GetPixels(), rgba.RowBytes, 0, 0))
        {
            throw new InvalidOperationException("Could not convert blurred pixels to straight-alpha RGBA.");
        }

        var pixels = new byte[checked(width * height * 4)];
        Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);
        return new RawImageData(width, height, pixels, cacheKey, revision);
    }
}

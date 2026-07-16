using ImageMagick;
using SkiaSharp;
using Svg.Skia;

namespace HyprNetShell.Rendering;

[AttributeUsage(AttributeTargets.Property)]
public sealed class SvgAssetAttribute(params string[] paths) : Attribute
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

public sealed class SvgAsset
{
    private const float MAX_RASTER_DIMENSION = 512.0f;
    private readonly byte[] _source;

    public string Path { get; }

    public SvgAsset(string path, string base64Source)
    {
        Path = path;
        _source = Convert.FromBase64String(base64Source);
    }

    public SvgRaster Rasterize()
    {
        using var source = new MemoryStream(_source, writable: false);
        using var svg = new SKSvg();
        var picture = svg.Load(source);
        if (picture is null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
        {
            throw new InvalidDataException($"SVG asset '{Path}' has no drawable content.");
        }

        var scale = MathF.Min(1.0f, MAX_RASTER_DIMENSION / MathF.Max(picture.CullRect.Width, picture.CullRect.Height));
        using var encoded = new MemoryStream();
        using var colorSpace = SKColorSpace.CreateSrgb();
        picture.ToImage(
            encoded, SKColors.Transparent, SKEncodedImageFormat.Png, 100, scale, scale,
            SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);
        encoded.Position = 0;

        using var image = new MagickImage(encoded);
        image.Format = MagickFormat.Rgba;
        var pixels = image.ToByteArray();
        if (pixels.Length != image.Width * image.Height * 4)
        {
            throw new InvalidDataException($"SVG asset '{Path}' produced an invalid RGBA image.");
        }

        return new SvgRaster((int)image.Width, (int)image.Height, pixels);
    }
}

public readonly record struct SvgRaster(int Width, int Height, ReadOnlyMemory<byte> Pixels);
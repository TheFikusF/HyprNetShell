using Svg.Skia;

namespace HyprNetShell.Rendering;

[AttributeUsage(AttributeTargets.Property)]
public sealed class SvgAssetAttribute(params string[] paths) : Attribute
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

public sealed class SvgAsset
{
    private readonly byte[] _source;

    public string Path
    {
        get;
    }

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

        return ImageDecoding.Rasterize(picture);
    }
}

public readonly record struct SvgRaster(int Width, int Height, ReadOnlyMemory<byte> Pixels);

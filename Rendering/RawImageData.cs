namespace HyprNetShell.Rendering;

/// <summary>Immutable straight-alpha RGBA pixels; keyed images share a texture and must change revision when pixels change.</summary>
public sealed record RawImageData(
    int Width,
    int Height,
    ReadOnlyMemory<byte> RgbaPixels,
    string? CacheKey = null,
    long Revision = 0)
{
    public void Deconstruct(out int width, out int height, out ReadOnlyMemory<byte> rgbaPixels)
    {
        width = Width;
        height = Height;
        rgbaPixels = RgbaPixels;
    }
}

public sealed record EncodedImageData(string MimeType, ReadOnlyMemory<byte> Bytes);

namespace HyprNetShell.Rendering;

public sealed record RawImageData(int Width, int Height, ReadOnlyMemory<byte> RgbaPixels);

public sealed record EncodedImageData(string MimeType, ReadOnlyMemory<byte> Bytes);

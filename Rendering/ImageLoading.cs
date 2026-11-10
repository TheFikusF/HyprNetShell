namespace HyprNetShell.Rendering;

public static class ImageLoading
{
    public static RawImageData LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        var image = ImageDecoding.Decode(stream);
        return new RawImageData(image.Width, image.Height, image.Pixels, path, 0);
    }
}

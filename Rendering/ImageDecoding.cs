using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SkiaSharp;

namespace HyprNetShell.Rendering;

internal static class ImageDecoding
{
    internal const int MAX_DECODED_IMAGE_BYTES = 64 * 1024 * 1024;
    private const int MAX_PAM_HEADER_BYTES = 4096;
    private const float MAX_SVG_DIMENSION = 512;

    internal static SvgRaster Decode(Stream stream, int decodeWidth = int.MaxValue, int decodeHeight = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodeWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(decodeHeight);
        var start = stream.Position;
        var pam = stream.ReadByte() == 'P' && stream.ReadByte() == '7';
        stream.Position = start;
        if (pam)
        {
            using var bitmap = DecodePam(stream);
            return Resize(bitmap, decodeWidth, decodeHeight);
        }

        using var codec = SKCodec.Create(stream)
            ?? throw new InvalidDataException("Unsupported or invalid image format.");
        var source = codec.Info;
        ValidateDimensions(source.Width, source.Height, enforceLimit: false);
        var scale = Math.Min(1d, Math.Min((double)decodeWidth / source.Width, (double)decodeHeight / source.Height));
        var sampled = codec.GetScaledDimensions((float)scale);
        ValidateDimensions(sampled.Width, sampled.Height);
        using var decoded = new SKBitmap(new SKImageInfo(sampled.Width, sampled.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var result = codec.GetPixels(decoded.Info, decoded.GetPixels());
        if (result != SKCodecResult.Success)
        {
            throw new InvalidDataException($"Image decode failed: {result}.");
        }

        return Resize(decoded, Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
    }

    internal static SvgRaster Rasterize(SKPicture picture)
    {
        var bounds = picture.CullRect;
        if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new InvalidDataException("SVG has no valid drawable bounds.");
        }

        var scale = MathF.Min(1, MAX_SVG_DIMENSION / MathF.Max(bounds.Width, bounds.Height));
        var width = Math.Max(1, (int)MathF.Ceiling(bounds.Width * scale));
        var height = Math.Max(1, (int)MathF.Ceiling(bounds.Height * scale));
        ValidateDimensions(width, height);
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }

        return Extract(bitmap);
    }

    private static SvgRaster Resize(SKBitmap source, int decodeWidth, int decodeHeight)
    {
        var scale = Math.Min(1d, Math.Min((double)decodeWidth / source.Width, (double)decodeHeight / source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        ValidateDimensions(width, height);
        if (width == source.Width && height == source.Height)
        {
            return Extract(source);
        }

        using var resized = source.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul),
            new SKSamplingOptions(SKCubicResampler.Mitchell))
            ?? throw new InvalidDataException("Could not resize image.");
        return Extract(resized);
    }

    private static SvgRaster Extract(SKBitmap bitmap)
    {
        ValidateDimensions(bitmap.Width, bitmap.Height);
        using var rgba = new SKBitmap(new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using var image = SKImage.FromBitmap(bitmap);
        if (!image.ReadPixels(rgba.Info, rgba.GetPixels(), rgba.RowBytes, 0, 0))
        {
            throw new InvalidDataException("Could not extract straight-alpha RGBA pixels.");
        }

        var pixels = new byte[checked(bitmap.Width * bitmap.Height * 4)];
        var rowBytes = checked(bitmap.Width * 4);
        for (var row = 0; row < bitmap.Height; row++)
        {
            Marshal.Copy(IntPtr.Add(rgba.GetPixels(), checked(row * rgba.RowBytes)), pixels, row * rowBytes, rowBytes);
        }

        return new SvgRaster(bitmap.Width, bitmap.Height, pixels);
    }

    private static SKBitmap DecodePam(Stream stream)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var headerBytes = 0;
        string ReadLine()
        {
            var line = new StringBuilder();
            while (++headerBytes <= MAX_PAM_HEADER_BYTES)
            {
                var value = stream.ReadByte();
                if (value < 0)
                {
                    throw new InvalidDataException("Truncated PAM header.");
                }

                if (value == '\n')
                {
                    return line.ToString().Trim();
                }

                line.Append((char)value);
            }

            throw new InvalidDataException("PAM header exceeds size limit.");
        }

        if (ReadLine() != "P7")
        {
            throw new InvalidDataException("Invalid PAM signature.");
        }

        while (true)
        {
            var line = ReadLine().Split('#', 2)[0].Trim();
            if (line == "ENDHDR")
            {
                break;
            }

            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || !fields.TryAdd(parts[0], parts[1].Trim()))
            {
                throw new InvalidDataException("Invalid or duplicate PAM header field.");
            }
        }

        if (fields.GetValueOrDefault("DEPTH") != "4" || fields.GetValueOrDefault("MAXVAL") != "255" ||
            fields.GetValueOrDefault("TUPLTYPE") != "RGB_ALPHA" ||
            !int.TryParse(fields.GetValueOrDefault("WIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(fields.GetValueOrDefault("HEIGHT"), NumberStyles.None, CultureInfo.InvariantCulture, out var height))
        {
            throw new InvalidDataException("Only 8-bit RGB_ALPHA PAM images are supported.");
        }

        ValidateDimensions(width, height);
        var pixels = new byte[checked(width * height * 4)];
        stream.ReadExactly(pixels);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        try
        {
            var rowBytes = checked(width * 4);
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(pixels, row * rowBytes, IntPtr.Add(bitmap.GetPixels(), checked(row * bitmap.RowBytes)), rowBytes);
            }

            return bitmap;
        }

        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private static void ValidateDimensions(int width, int height, bool enforceLimit = true)
    {
        if (width <= 0 || height <= 0 || (enforceLimit && (long)width * height > MAX_DECODED_IMAGE_BYTES / 4))
        {
            throw new InvalidDataException($"Invalid or oversized image dimensions: {width}x{height}.");
        }
    }
}

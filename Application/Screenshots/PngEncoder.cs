using System.Buffers.Binary;
using System.IO.Compression;

namespace HyprNetShell.Application.Screenshots;

internal static class PngEncoder
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal static byte[] EncodeBgra(
        HyprLayer.CapturedImage image,
        int x,
        int y,
        int width,
        int height)
    {
        x = Math.Clamp(x, 0, image.Width - 1);
        y = Math.Clamp(y, 0, image.Height - 1);
        width = Math.Clamp(width, 1, image.Width - x);
        height = Math.Clamp(height, 1, image.Height - y);

        using var output = new MemoryStream();
        output.Write(Signature);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var row = new byte[1 + width * 4];
            for (var rowIndex = 0; rowIndex < height; rowIndex++)
            {
                row[0] = 0;
                var source = (y + rowIndex) * image.Stride + x * 4;
                for (var column = 0; column < width; column++)
                {
                    var sourcePixel = source + column * 4;
                    var targetPixel = 1 + column * 4;
                    row[targetPixel] = image.BgraPixels[sourcePixel + 2];
                    row[targetPixel + 1] = image.BgraPixels[sourcePixel + 1];
                    row[targetPixel + 2] = image.BgraPixels[sourcePixel];
                    row[targetPixel + 3] = 255;
                }
                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, checked((int)compressed.Length)));
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> value = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(value, (uint)data.Length);
        output.Write(value);
        output.Write(type);
        output.Write(data);

        var crc = 0xffffffffu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        BinaryPrimitives.WriteUInt32BigEndian(value, ~crc);
        output.Write(value);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            }
        }
        return crc;
    }
}

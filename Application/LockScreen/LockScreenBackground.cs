using System.Net.Sockets;
using System.Text;
using HyprNetShell.Rendering;

namespace HyprNetShell.Application.LockScreen;

internal static class LockScreenBackground
{
    private const int TargetWidth = 480;
    private const int BlurRadius = 8;
    private const int ProtocolMagic = 0x484e5342;
    private const int MaximumOutputs = 16;
    private const int MaximumImageBytes = 64 * 1024 * 1024;

    internal static Transfer CaptureAndServe(HyprLayer layer)
    {
        var backgrounds = new Dictionary<string, RawImageData>(StringComparer.Ordinal);
        foreach (var output in layer.Outputs)
        {
            var capture = layer.CaptureOutput(output.Id);
            if (capture is not null && !string.IsNullOrWhiteSpace(output.Name))
            {
                try
                {
                    backgrounds[output.Name] = DownsampleAndBlur(capture);
                }
                finally
                {
                    Array.Clear(capture.BgraPixels);
                }
            }
        }

        return new Transfer(backgrounds);
    }

    internal static IReadOnlyDictionary<string, RawImageData> Receive(string? token)
    {
        var backgrounds = new Dictionary<string, RawImageData>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(token))
        {
            return backgrounds;
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.ConnectAsync(new UnixDomainSocketEndPoint("\0" + token), timeout.Token).AsTask().GetAwaiter().GetResult();
        using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        if (reader.ReadInt32() != ProtocolMagic)
        {
            throw new InvalidDataException("Invalid lock background transfer.");
        }

        var count = reader.ReadInt32();
        if (count < 0 || count > MaximumOutputs)
        {
            throw new InvalidDataException("Invalid lock background output count.");
        }

        for (var index = 0; index < count; index++)
        {
            var nameLength = reader.ReadInt32();
            if (nameLength <= 0 || nameLength > 4096)
            {
                throw new InvalidDataException("Invalid lock background output name.");
            }
            var name = Encoding.UTF8.GetString(reader.ReadBytesExactly(nameLength));
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var length = reader.ReadInt32();
            if (width <= 0 || height <= 0 || length != checked(width * height * 4) || length > MaximumImageBytes)
            {
                throw new InvalidDataException("Invalid lock background dimensions.");
            }
            backgrounds[name] = new RawImageData(width, height, reader.ReadBytesExactly(length));
        }

        return backgrounds;
    }

    internal sealed class Transfer : IDisposable
    {
        private readonly Dictionary<string, RawImageData> _backgrounds;
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(10));
        private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        private readonly Task _sendTask;
        private int _disposed;

        internal Transfer(Dictionary<string, RawImageData> backgrounds)
        {
            _backgrounds = backgrounds;
            Token = "hyprnetshell-lock-" + Guid.NewGuid().ToString("N");
            _listener.Bind(new UnixDomainSocketEndPoint("\0" + Token));
            _listener.Listen(1);
            _sendTask = Task.Run(SendAsync);
        }

        internal string Token { get; }

        private async Task SendAsync()
        {
            try
            {
                using var client = await _listener.AcceptAsync(_lifetime.Token);
                using var stream = new NetworkStream(client, ownsSocket: false);
                using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
                writer.Write(ProtocolMagic);
                writer.Write(_backgrounds.Count);
                foreach (var (name, image) in _backgrounds)
                {
                    var nameBytes = Encoding.UTF8.GetBytes(name);
                    writer.Write(nameBytes.Length);
                    writer.Write(nameBytes);
                    writer.Write(image.Width);
                    writer.Write(image.Height);
                    writer.Write(image.RgbaPixels.Length);
                    writer.Write(image.RgbaPixels.Span);
                }
                writer.Flush();
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                foreach (var image in _backgrounds.Values)
                {
                    if (image.RgbaPixels.TryGetArray(out var segment) && segment.Array is not null)
                    {
                        Array.Clear(segment.Array);
                    }
                }
                Dispose();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            _lifetime.Cancel();
            _listener.Dispose();
            _lifetime.Dispose();
        }
    }

    private static RawImageData DownsampleAndBlur(HyprLayer.CapturedImage source)
    {
        var width = Math.Min(TargetWidth, source.Width);
        var height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sourceY = Math.Min(source.Height - 1, y * source.Height / height);
            for (var x = 0; x < width; x++)
            {
                var sourceX = Math.Min(source.Width - 1, x * source.Width / width);
                var sourceOffset = sourceY * source.Stride + sourceX * 4;
                var targetOffset = (y * width + x) * 4;
                pixels[targetOffset] = source.BgraPixels[sourceOffset + 2];
                pixels[targetOffset + 1] = source.BgraPixels[sourceOffset + 1];
                pixels[targetOffset + 2] = source.BgraPixels[sourceOffset];
                pixels[targetOffset + 3] = 255;
            }
        }

        var scratch = new byte[pixels.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BlurHorizontal(pixels, scratch, width, height);
            BlurVertical(scratch, pixels, width, height);
        }
        return new RawImageData(width, height, pixels);
    }

    private static void BlurHorizontal(byte[] source, byte[] target, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Average(source, target, width, height, x - BlurRadius, y, x + BlurRadius, y, x, y);
            }
        }
    }

    private static void BlurVertical(byte[] source, byte[] target, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                Average(source, target, width, height, x, y - BlurRadius, x, y + BlurRadius, x, y);
            }
        }
    }

    private static void Average(
        byte[] source,
        byte[] target,
        int width,
        int height,
        int startX,
        int startY,
        int endX,
        int endY,
        int targetX,
        int targetY)
    {
        startX = Math.Clamp(startX, 0, width - 1);
        endX = Math.Clamp(endX, 0, width - 1);
        startY = Math.Clamp(startY, 0, height - 1);
        endY = Math.Clamp(endY, 0, height - 1);
        var red = 0;
        var green = 0;
        var blue = 0;
        var count = 0;
        for (var y = startY; y <= endY; y++)
        {
            for (var x = startX; x <= endX; x++)
            {
                var offset = (y * width + x) * 4;
                red += source[offset];
                green += source[offset + 1];
                blue += source[offset + 2];
                count++;
            }
        }
        var targetOffset = (targetY * width + targetX) * 4;
        target[targetOffset] = (byte)(red / count);
        target[targetOffset + 1] = (byte)(green / count);
        target[targetOffset + 2] = (byte)(blue / count);
        target[targetOffset + 3] = 255;
    }

    private static byte[] ReadBytesExactly(this BinaryReader reader, int length)
    {
        var result = reader.ReadBytes(length);
        return result.Length == length ? result : throw new EndOfStreamException();
    }

    private static bool TryGetArray(this ReadOnlyMemory<byte> memory, out ArraySegment<byte> segment) =>
        System.Runtime.InteropServices.MemoryMarshal.TryGetArray(memory, out segment);
}

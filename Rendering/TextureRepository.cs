#pragma warning disable CA1416

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime;
using System.Security.Cryptography;
using ImageMagick;
using SkiaSharp;
using Silk.NET.OpenGL;
using Svg.Skia;

namespace HyprNetShell.Rendering;

public readonly record struct Texture(uint Id);

public sealed unsafe class TextureRepository : IDisposable
{
    private const int MAX_DECODED_IMAGE_BYTES = 64 * 1024 * 1024;
    private static readonly TimeSpan PathTextureIdleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PathTextureCleanupInterval = TimeSpan.FromSeconds(1);

    private static readonly ConcurrentExclusiveSchedulerPair PathImageDecodeScheduler =
        new(TaskScheduler.Default, maxConcurrencyLevel: 2);

    private readonly GL _gl;
    private readonly Dictionary<PathTextureKey, PathTexture> _pathTextures = [];
    private readonly Dictionary<PathTextureKey, PendingPathTexture> _pendingPathTextures = [];
    private readonly Dictionary<RawTextureKey, Texture> _rawTextures = [];
    private readonly Dictionary<RawImageData, Texture> _imageTextures = [];
    private readonly Dictionary<EncodedImageData, Texture> _encodedImageTextures = [];
    private readonly Dictionary<SvgAsset, Texture> _assetTextures = [];

    private readonly Queue<PathTextureKey> _pathTextureKeysBuffer = new();

    private long _lastPathTextureCleanupTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

    static TextureRepository()
    {
        ResourceLimits.Area = 1_000_000;
        ResourceLimits.Memory = 32 * 1024 * 1024;
        ResourceLimits.MaxMemoryRequest = 16 * 1024 * 1024;
    }

    public TextureRepository(GL gl)
    {
        _gl = gl;
    }

    public Texture? GetTexture(
        string path,
        int decodeWidth = int.MaxValue,
        int decodeHeight = int.MaxValue,
        bool loadAsync = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            RemoveUnusedPathResources();

            if (!File.Exists(path))
            {
                return null;
            }

            path = Path.GetFullPath(path);
            var modified = File.GetLastWriteTimeUtc(path);
            var key = new PathTextureKey(path, decodeWidth, decodeHeight);
            if (_pathTextures.TryGetValue(key, out var cached) && cached.Modified == modified)
            {
                _pathTextures[key] = cached with { LastAccessTimestamp = Stopwatch.GetTimestamp() };
                return cached.Texture;
            }

            if (cached.Texture.Id != 0)
            {
                _gl.DeleteTexture(cached.Texture.Id);
                _pathTextures.Remove(key);
            }

            var image = loadAsync
                ? GetAsyncDecodedImage(key, modified, path, decodeWidth, decodeHeight)
                : LoadImage(path, decodeWidth, decodeHeight);
            if (image is null)
            {
                return null;
            }

            if (_pendingPathTextures.Remove(key, out var completedDecode))
            {
                completedDecode.Cancellation.Dispose();
            }

            var texture = UploadTexture(image.Value.Pixels, image.Value.Width, image.Value.Height);
            _pathTextures[key] = new PathTexture(texture, modified, Stopwatch.GetTimestamp());
            return texture;
        }
        catch
        {
            return null;
        }
    }

    public void RemoveUnusedPathResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastPathTextureCleanupTimestamp, now) < PathTextureCleanupInterval)
        {
            return;
        }

        _lastPathTextureCleanupTimestamp = now;
        var removedResources = false;
        foreach (var (key, _) in _pathTextures.Where(x =>
                     Stopwatch.GetElapsedTime(x.Value.LastAccessTimestamp, now) >= PathTextureIdleTimeout))
        {
            _pathTextureKeysBuffer.Enqueue(key);
        }

        while (_pathTextureKeysBuffer.TryDequeue(out var key) && _pathTextures.Remove(key, out var texture))
        {
            _gl.DeleteTexture(texture.Texture.Id);
            removedResources = true;
        }

        foreach (var (key, _) in _pendingPathTextures.Where(x =>
                     Stopwatch.GetElapsedTime(x.Value.LastAccessTimestamp, now) >= PathTextureIdleTimeout))
        {
            _pathTextureKeysBuffer.Enqueue(key);
        }

        while (_pathTextureKeysBuffer.TryDequeue(out var key) && _pendingPathTextures.Remove(key, out var pending))
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
            removedResources = true;
        }

        if (!removedResources)
        {
            return;
        }

        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        ResourceLimits.TrimMemory();
    }

    public Texture GetTexture(ReadOnlySpan<byte> rgbaPixels, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRawTexture(rgbaPixels, width, height);

        var key = CreateRawTextureKey(rgbaPixels, width, height);
        if (_rawTextures.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var texture = UploadTexture(rgbaPixels, width, height);
        _rawTextures[key] = texture;
        return texture;
    }

    public Texture GetTexture(RawImageData image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRawTexture(image.RgbaPixels.Span, image.Width, image.Height);

        if (_imageTextures.TryGetValue(image, out var cached))
        {
            return cached;
        }

        var texture = UploadTexture(image.RgbaPixels.Span, image.Width, image.Height);
        _imageTextures[image] = texture;
        return texture;
    }

    public Texture? GetTexture(EncodedImageData image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_encodedImageTextures.TryGetValue(image, out var cached))
        {
            return cached;
        }

        try
        {
            using var decoded = new MagickImage(image.Bytes.ToArray());
            var pixels = DecodeMagickImage(decoded);
            if (pixels is null)
            {
                return null;
            }

            var texture = UploadTexture(pixels.Value.Pixels, pixels.Value.Width, pixels.Value.Height);
            _encodedImageTextures[image] = texture;
            return texture;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    public Texture? GetTexture(SvgAsset asset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_assetTextures.TryGetValue(asset, out var cached))
        {
            return cached;
        }

        try
        {
            var raster = asset.Rasterize();
            var texture = GetTexture(raster.Pixels.Span, raster.Width, raster.Height);
            _assetTextures[asset] = texture;
            return texture;
        }
        catch
        {
            return null;
        }
    }

    private DecodedImage? GetAsyncDecodedImage(
        PathTextureKey key,
        DateTime modified,
        string path,
        int decodeWidth,
        int decodeHeight)
    {
        if (_pendingPathTextures.TryGetValue(key, out var pending))
        {
            if (pending.Modified == modified)
            {
                _pendingPathTextures[key] = pending with { LastAccessTimestamp = Stopwatch.GetTimestamp() };
                if (!pending.Decode.IsCompleted)
                {
                    return null;
                }

                try
                {
                    return pending.Decode.GetAwaiter().GetResult();
                }
                catch
                {
                    return null;
                }
            }

            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
            _pendingPathTextures.Remove(key);
        }

        var cancellation = new CancellationTokenSource();
        _pendingPathTextures[key] = new PendingPathTexture(
            modified,
            QueuePathImageDecode(path, decodeWidth, decodeHeight, cancellation.Token),
            cancellation,
            Stopwatch.GetTimestamp());
        return null;
    }

    private static Task<DecodedImage?> QueuePathImageDecode(
        string path,
        int decodeWidth,
        int decodeHeight,
        CancellationToken cancellationToken) => Task.Factory.StartNew(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return LoadImage(path, decodeWidth, decodeHeight);
        },
        cancellationToken,
        TaskCreationOptions.DenyChildAttach,
        PathImageDecodeScheduler.ConcurrentScheduler);

    private Texture UploadTexture(ReadOnlySpan<byte> rgbaPixels, int width, int height)
    {
        var id = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, id);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        fixed (byte* data = rgbaPixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                data);
        }

        return new Texture(id);
    }

    private static void ValidateRawTexture(ReadOnlySpan<byte> rgbaPixels, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var expectedLength = checked(width * height * 4);
        if (rgbaPixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Expected {expectedLength} bytes for a {width}x{height} RGBA texture, but received {rgbaPixels.Length}.",
                nameof(rgbaPixels));
        }
    }

    private static RawTextureKey CreateRawTextureKey(ReadOnlySpan<byte> rgbaPixels, int width, int height)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(rgbaPixels, hash);
        return new RawTextureKey(
            width,
            height,
            BinaryPrimitives.ReadUInt64LittleEndian(hash),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(hash[24..]));
    }

    private static DecodedImage? LoadImage(string path, int decodeWidth, int decodeHeight)
    {
        try
        {
            return Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
                   Path.GetExtension(path).Equals(".svgz", StringComparison.OrdinalIgnoreCase)
                ? LoadSvg(path)
                : LoadRasterImage(path, decodeWidth, decodeHeight);
        }
        finally
        {
            ResourceLimits.TrimMemory();
        }
    }

    private static DecodedImage? LoadSvg(string path)
    {
        try
        {
            using var svg = new SKSvg();
            var picture = svg.Load(path);
            if (picture is null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0)
            {
                return null;
            }

            const float MAX_DIMENSION = 512.0f;
            var scale = MathF.Min(1.0f, MAX_DIMENSION / MathF.Max(picture.CullRect.Width, picture.CullRect.Height));
            using var stream = new MemoryStream();
            using var colorSpace = SKColorSpace.CreateSrgb();
            picture.ToImage(
                stream, SKColors.Transparent, SKEncodedImageFormat.Png, 100, scale, scale,
                SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);
            stream.Position = 0;
            using var image = new MagickImage(stream);
            return DecodeMagickImage(image);
        }
        catch
        {
            return null;
        }
    }

    private static DecodedImage? LoadRasterImage(string path, int decodeWidth, int decodeHeight)
    {
        try
        {
            using var image = new MagickImage(path);
            if (image.Width > decodeWidth || image.Height > decodeHeight)
            {
                image.Resize((uint)decodeWidth, (uint)decodeHeight);
            }

            return DecodeMagickImage(image);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static DecodedImage? DecodeMagickImage(MagickImage image)
    {
        if ((long)image.Width * image.Height * 4 > MAX_DECODED_IMAGE_BYTES)
        {
            return null;
        }

        image.Format = MagickFormat.Rgba;
        var pixels = image.ToByteArray();
        return pixels.Length == image.Width * image.Height * 4
            ? new DecodedImage((int)image.Width, (int)image.Height, pixels)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var texture in _pathTextures.Values)
        {
            _gl.DeleteTexture(texture.Texture.Id);
        }

        foreach (var texture in _rawTextures.Values)
        {
            _gl.DeleteTexture(texture.Id);
        }

        foreach (var texture in _imageTextures.Values)
        {
            _gl.DeleteTexture(texture.Id);
        }

        foreach (var texture in _encodedImageTextures.Values)
        {
            _gl.DeleteTexture(texture.Id);
        }

        _pathTextures.Clear();
        foreach (var pending in _pendingPathTextures.Values)
        {
            pending.Cancellation.Cancel();
            pending.Cancellation.Dispose();
        }

        _pendingPathTextures.Clear();
        _rawTextures.Clear();
        _imageTextures.Clear();
        _encodedImageTextures.Clear();
        _assetTextures.Clear();
        _disposed = true;
    }

    private readonly record struct PathTextureKey(string Path, int Width, int Height);

    private readonly record struct PathTexture(Texture Texture, DateTime Modified, long LastAccessTimestamp);

    private readonly record struct PendingPathTexture(
        DateTime Modified,
        Task<DecodedImage?> Decode,
        CancellationTokenSource Cancellation,
        long LastAccessTimestamp);

    private readonly record struct RawTextureKey(int Width, int Height, ulong A, ulong B, ulong C, ulong D);

    private readonly record struct DecodedImage(int Width, int Height, byte[] Pixels);
}
#pragma warning disable CA1416

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime;
using System.Security.Cryptography;
using Silk.NET.OpenGL;
using Svg.Skia;

namespace HyprNetShell.Rendering;

public readonly record struct Texture(uint Id);

public sealed unsafe class TextureRepository : IDisposable
{
    private readonly record struct LiveTexture(Texture Texture, int Width, int Height, long Revision, bool UsedThisFrame);

    private readonly record struct PathTextureKey(string Path, int Width, int Height);

    private readonly record struct PathTexture(Texture Texture, DateTime Modified, long LastAccessTimestamp);

    private readonly record struct PendingPathTexture(
        DateTime Modified,
        Task<DecodedImage?> Decode,
        CancellationTokenSource Cancellation,
        long LastAccessTimestamp);

    private readonly record struct RawTextureKey(int Width, int Height, ulong A, ulong B, ulong C, ulong D);

    private readonly record struct DecodedImage(int Width, int Height, byte[] Pixels);

    private static readonly TimeSpan PathTextureIdleTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PathTextureCleanupInterval = TimeSpan.FromSeconds(1);

    private static readonly ConcurrentExclusiveSchedulerPair PathImageDecodeScheduler =
        new(TaskScheduler.Default, maxConcurrencyLevel: 2);

    private readonly GL _gl;
    private readonly Dictionary<PathTextureKey, PathTexture> _pathTextures = [];
    private readonly Dictionary<PathTextureKey, PendingPathTexture> _pendingPathTextures = [];
    private readonly Dictionary<RawTextureKey, Texture> _rawTextures = [];
    private readonly Dictionary<RawImageData, Texture> _imageTextures = [];
    private readonly Dictionary<string, LiveTexture> _liveTextures = new(StringComparer.Ordinal);
    private readonly Queue<string> _liveTextureKeysBuffer = new();
    private readonly Dictionary<EncodedImageData, Texture> _encodedImageTextures = [];
    private readonly Dictionary<SvgAsset, Texture> _assetTextures = [];

    private readonly Queue<PathTextureKey> _pathTextureKeysBuffer = new();

    private long _lastPathTextureCleanupTimestamp = Stopwatch.GetTimestamp();
    private bool _disposed;

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
            // Animated draw bounds should not queue a decode for every pixel of growth.
            decodeWidth = BucketDecodeSize(decodeWidth);
            decodeHeight = BucketDecodeSize(decodeHeight);
            var key = new PathTextureKey(path, decodeWidth, decodeHeight);
            if (_pathTextures.TryGetValue(key, out var cached) && cached.Modified == modified)
            {
                _pathTextures[key] = cached with {
                    LastAccessTimestamp = Stopwatch.GetTimestamp()
                };
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
                return loadAsync ? GetCachedPathTexture(path, modified, decodeWidth, decodeHeight) : null;
            }

            if (_pendingPathTextures.Remove(key, out var completedDecode))
            {
                completedDecode.Cancellation.Dispose();
            }

            var texture = UploadTexture(image.Value.Pixels, image.Value.Width, image.Value.Height);
            _pathTextures[key] = new PathTexture(texture, modified, Stopwatch.GetTimestamp());
            return texture;
        }

        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Rendering] Failed to load texture: {exception}");
            return null;
        }
    }

    private static int BucketDecodeSize(int size) =>
        size >= int.MaxValue - 31 ? int.MaxValue : ((Math.Max(1, size) + 31) / 32) * 32;

    private Texture? GetCachedPathTexture(string path, DateTime modified, int width, int height)
    {
        PathTextureKey? closestKey = null;
        var closestDistance = long.MaxValue;
        foreach (var (key, cached) in _pathTextures)
        {
            if (key.Path != path || cached.Modified != modified)
            {
                continue;
            }

            var distance = Math.Abs((long)key.Width - width) + Math.Abs((long)key.Height - height);
            if (distance < closestDistance)
            {
                closestKey = key;
                closestDistance = distance;
            }
        }

        if (closestKey is not { } selected)
        {
            return null;
        }

        var texture = _pathTextures[selected];
        _pathTextures[selected] = texture with {
            LastAccessTimestamp = Stopwatch.GetTimestamp()
        };
        return texture.Texture;
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

    public bool NeedsUpload(RawImageData image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (image.CacheKey is not { } key)
        {
            return !_imageTextures.ContainsKey(image);
        }

        return !_liveTextures.TryGetValue(key, out var live)
            || live.Revision != image.Revision
            || live.Width != image.Width
            || live.Height != image.Height;
    }

    public Texture GetTexture(RawImageData image)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateRawTexture(image.RgbaPixels.Span, image.Width, image.Height);

        if (!(image.CacheKey is { } key))
        {
            if (_imageTextures.TryGetValue(image, out var cached))
            {
                return cached;
            }

            var texture = UploadTexture(image.RgbaPixels.Span, image.Width, image.Height);
            _imageTextures[image] = texture;
            return texture;
        }

        if (!_liveTextures.TryGetValue(key, out var live))
        {
            var liveTexture = UploadTexture(image.RgbaPixels.Span, image.Width, image.Height);
            _liveTextures[key] = new LiveTexture(liveTexture, image.Width, image.Height, image.Revision, true);
            return liveTexture;
        }

        if (live.Revision != image.Revision || live.Width != image.Width || live.Height != image.Height)
        {
            _gl.BindTexture(TextureTarget.Texture2D, live.Texture.Id);
            fixed (byte* data = image.RgbaPixels.Span)
            {
                if (live.Width != image.Width || live.Height != image.Height)
                {
                    _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
                        (uint)image.Width, (uint)image.Height, 0,
                        PixelFormat.Rgba, PixelType.UnsignedByte, data);
                }

                else
                {
                    _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0,
                        (uint)image.Width, (uint)image.Height,
                        PixelFormat.Rgba, PixelType.UnsignedByte, data);
                }
            }
        }

        _liveTextures[key] = new LiveTexture(live.Texture, image.Width, image.Height, image.Revision, true);
        return live.Texture;
    }

    /// <summary>Call once after all outputs and overlays; drops keyed images unused this application frame.</summary>
    public void RemoveUnusedLiveResources()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var key in _liveTextures.Keys)
        {
            _liveTextureKeysBuffer.Enqueue(key);
        }

        while (_liveTextureKeysBuffer.TryDequeue(out var key))
        {
            var texture = _liveTextures[key];
            if (texture.UsedThisFrame)
            {
                _liveTextures[key] = texture with {
                    UsedThisFrame = false
                };
            }

            else
            {
                _gl.DeleteTexture(texture.Texture.Id);
                _liveTextures.Remove(key);
            }
        }
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
            using var stream = new MemoryStream(image.Bytes.ToArray(), writable: false);
            var raster = ImageDecoding.Decode(stream);
            var pixels = new DecodedImage(raster.Width, raster.Height, raster.Pixels.ToArray());

            var texture = UploadTexture(pixels.Pixels, pixels.Width, pixels.Height);
            _encodedImageTextures[image] = texture;
            return texture;
        }

        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Rendering] Failed to decode encoded image: {exception}");
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

        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Rendering] Failed to load texture: {exception}");
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
                _pendingPathTextures[key] = pending with {
                    LastAccessTimestamp = Stopwatch.GetTimestamp()
                };
                if (!pending.Decode.IsCompleted)
                {
                    return null;
                }

                try
                {
                    return pending.Decode.GetAwaiter().GetResult();
                }

                catch (OperationCanceledException)
                {
                    return null;
                }

                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Rendering] Failed to complete image decode '{path}': {exception}");
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
            SvgRaster raster;
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".svgz", StringComparison.OrdinalIgnoreCase))
            {
                using var svg = new SKSvg();
                var picture = svg.Load(path)
                    ?? throw new InvalidDataException("SVG has no drawable content.");
                raster = ImageDecoding.Rasterize(picture);
            }

            else
            {
                using var stream = File.OpenRead(path);
                raster = ImageDecoding.Decode(stream, decodeWidth, decodeHeight);
            }

            return new DecodedImage(raster.Width, raster.Height, raster.Pixels.ToArray());
        }

        catch (Exception exception)
        {
            Console.Error.WriteLine($"[Rendering] Failed to decode image '{path}': {exception}");
            return null;
        }
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

        foreach (var texture in _liveTextures.Values)
        {
            _gl.DeleteTexture(texture.Texture.Id);
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
        _liveTextures.Clear();
        _liveTextureKeysBuffer.Clear();
        _encodedImageTextures.Clear();
        _assetTextures.Clear();
        _disposed = true;
    }

}

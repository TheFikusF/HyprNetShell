using HyprNetShell.Core.Logging;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Overview;

internal sealed class OverviewBackgroundService(Func<string?> currentWallpaper) : IDisposable
{
    private readonly Lock _gate = new();
    private readonly string _cacheKey = $"overview-wallpaper:{Guid.NewGuid():N}";
    private CancellationTokenSource? _loading;
    private Task _task = Task.CompletedTask;
    private string? _path;
    private RawImageData? _image;
    private long _revision;
    private bool _disposed;

    internal RawImageData? GetImage()
    {
        var path = currentWallpaper();
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (string.Equals(path, _path, StringComparison.Ordinal))
            {
                return _image;
            }

            _path = path;
            _image = null;
            _loading?.Cancel();
            _loading?.Dispose();
            _loading = null;
            var revision = ++_revision;
            if (!string.IsNullOrWhiteSpace(path))
            {
                _loading = new CancellationTokenSource();
                var token = _loading.Token;
                var previous = _task;
                // Serialize native decoding even when a wallpaper changes during an uncancellable Skia operation.
                _task = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    try
                    {
                        var image = ImageBlur.BlurFile(path, _cacheKey, revision, token);
                        lock (_gate)
                        {
                            if (!_disposed && !token.IsCancellationRequested && revision == _revision)
                            {
                                _image = image;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception exception)
                    {
                        AppLogger.Warning("Overview", $"Could not load blurred wallpaper '{path}'", exception);
                    }
                });
            }
            return null;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _image = null;
            _path = null;
            _loading?.Cancel();
            _loading?.Dispose();
            _loading = null;
        }
    }
}

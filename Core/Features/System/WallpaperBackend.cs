using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HyprNetShell.Core.Logging;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Features.System;

internal sealed class WallpaperBackend : IDisposable
{
    private static class Native
    {
        private const string LIBRARY = "hypr_layer";

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hypr_wallpaper_create();

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void hypr_wallpaper_destroy(IntPtr handle);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hypr_wallpaper_poll(IntPtr handle);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int hypr_wallpaper_set_image(IntPtr handle, in byte rgba, int width, int height, int stride);

        [DllImport(LIBRARY, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr hypr_wallpaper_get_error(IntPtr handle);
    }

    private sealed record Request(string Path, CancellationToken Cancellation, TaskCompletionSource<bool> Completion);

    private readonly BlockingCollection<Request> _requests = new();
    private readonly Lock _gate = new();
    private readonly Task _worker;
    private bool _disposed;
    private bool _stopped;

    internal WallpaperBackend()
    {
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal Task<bool> SetAsync(string path, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<bool>(cancellationToken);
            }

            if (_disposed || _stopped)
            {
                return Task.FromResult(false);
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _requests.Add(new Request(path, cancellationToken, completion));
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private void Run()
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = Native.hypr_wallpaper_create();
            if (handle == IntPtr.Zero)
            {
                throw new InvalidOperationException(GetError(handle));
            }

            while (!Volatile.Read(ref _disposed) && !_requests.IsCompleted)
            {
                if (_requests.TryTake(out var request, 16))
                {
                    try
                    {
                        if (Volatile.Read(ref _disposed))
                        {
                            request.Completion.TrySetResult(false);
                            break;
                        }

                        request.Cancellation.ThrowIfCancellationRequested();
                        var image = ImageLoading.LoadFile(request.Path);
                        request.Cancellation.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref _disposed))
                        {
                            request.Completion.TrySetResult(false);
                            break;
                        }

                        var success = Native.hypr_wallpaper_set_image(
                            handle,
                            in MemoryMarshal.GetReference(image.RgbaPixels.Span),
                            image.Width,
                            image.Height,
                            checked(image.Width * 4)) != 0;
                        if (!success)
                        {
                            AppLogger.Warning("Wallpapers", $"Could not apply '{request.Path}': {GetError(handle)}");
                        }

                        request.Completion.TrySetResult(success);
                    }

                    catch (OperationCanceledException) when (request.Cancellation.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled(request.Cancellation);
                    }

                    catch (Exception exception)
                    {
                        AppLogger.Warning("Wallpapers", $"Could not load '{request.Path}'", exception);
                        request.Completion.TrySetResult(false);
                    }
                }

                if (Native.hypr_wallpaper_poll(handle) == 0)
                {
                    throw new InvalidOperationException(GetError(handle));
                }
            }
        }

        catch (Exception exception)
        {
            AppLogger.Error("Wallpapers", "Wallpaper backend stopped", exception);
        }

        finally
        {
            lock (_gate)
            {
                _stopped = true;
                _requests.CompleteAdding();
            }

            while (_requests.TryTake(out var request))
            {
                if (request.Cancellation.IsCancellationRequested)
                {
                    request.Completion.TrySetCanceled(request.Cancellation);
                }

                else
                {
                    request.Completion.TrySetResult(false);
                }
            }

            try
            {
                if (handle != IntPtr.Zero)
                {
                    Native.hypr_wallpaper_destroy(handle);
                }
            }

            catch (Exception exception)
            {
                AppLogger.Error("Wallpapers", "Could not destroy wallpaper backend", exception);
            }

            finally
            {
                _requests.Dispose();
            }
        }
    }

    private static string GetError(IntPtr handle) =>
        Marshal.PtrToStringUTF8(Native.hypr_wallpaper_get_error(handle)) ?? "Unknown Wayland wallpaper error";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            if (!_stopped)
            {
                _requests.CompleteAdding();
            }
        }

        if (!_worker.Wait(TimeSpan.FromSeconds(3)))
        {
            AppLogger.Warning("Wallpapers", "Wallpaper worker is still finishing an image operation during shutdown");
        }
    }
}

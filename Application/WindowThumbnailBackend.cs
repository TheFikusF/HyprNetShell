using System.Text;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;
using HyprNetShell.Rendering;

namespace HyprNetShell.Application;

internal sealed class WindowThumbnailBackend : IWindowThumbnailBackend
{
    private readonly HyprLayer _layer;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<ulong, ulong> _revisions = [];
    private readonly Dictionary<ulong, string> _errors = [];
    private IReadOnlyList<WindowThumbnailMetadata> _windows = [];
    private ulong? _serial;
    private bool _disposed;

    internal WindowThumbnailBackend(HyprLayer layer)
    {
        _layer = layer;
        SetEnabled(false);
    }

    public bool IsAvailable
    {
        get
        {
            CheckThread();
            if (_disposed || _layer.NativeHandle == IntPtr.Zero)
            {
                return false;
            }

            LogNativeError(0);
            return NativeMethods.hypr_layer_thumbnails_available(_layer.NativeHandle) != 0;
        }
    }

    public IReadOnlyList<WindowThumbnailMetadata> PollWindows()
    {
        var native = GetHandle();
        var serial = NativeMethods.hypr_layer_get_window_serial(native);
        if (_serial != serial)
        {
            var windows = new List<WindowThumbnailMetadata>();
            var count = NativeMethods.hypr_layer_get_window_count(native);
            for (var index = 0; index < count; index++)
            {
                var id = NativeMethods.hypr_layer_get_window_id(native, index);
                if (id == 0)
                {
                    continue;
                }

                windows.Add(new(id,
                    ReadString(id, NativeMethods.hypr_layer_get_window_title),
                    ReadString(id, NativeMethods.hypr_layer_get_window_app_id),
                    ReadString(id, NativeMethods.hypr_layer_get_window_identifier),
                    ReadString(id, NativeMethods.hypr_layer_get_window_address)));
            }

            var ids = windows.Select(window => window.Handle).ToHashSet();
            foreach (var id in _revisions.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                _revisions.Remove(id);
            }

            foreach (var id in _errors.Keys.Where(id => id != 0 && !ids.Contains(id)).ToArray())
            {
                _errors.Remove(id);
            }

            _windows = windows.AsReadOnly();
            _serial = serial;
        }

        foreach (var window in _windows)
        {
            LogNativeError(window.Handle);
        }

        return _windows;
    }

    public IReadOnlyList<WindowThumbnailFrame> PollFrames()
    {
        var native = GetHandle();
        var frames = new List<WindowThumbnailFrame>();
        foreach (var window in _windows)
        {
            var id = window.Handle;
            LogNativeError(id);
            if (NativeMethods.hypr_layer_get_window_thumbnail_info(native, id,
                    out var revision, out var width, out var height, out var stride) == 0 ||
                revision == 0 || (_revisions.TryGetValue(id, out var previous) && revision <= previous))
            {
                continue;
            }

            if (width <= 0 || width > 640 || height <= 0 || height > 360 || (long)width * 4 != stride)
            {
                LogError(id, "Native thumbnail has invalid RGBA dimensions or stride.");
                continue;
            }
            var pixels = new byte[checked(stride * height)];
            if (NativeMethods.hypr_layer_copy_window_thumbnail(native, id, revision, pixels, pixels.Length) != pixels.Length)
            {
                LogError(id, "Could not copy the native thumbnail at its advertised revision.");
                continue;
            }

            // Each published frame owns a fresh buffer, never reused or mutated afterwards.
            frames.Add(new(id, new RawImageData(width, height, pixels, $"window-thumbnail:{id}", unchecked((long)revision)), revision));
            _revisions[id] = revision;
        }
        return frames.AsReadOnly();
    }

    public void SetEnabled(bool enabled)
    {
        if (NativeMethods.hypr_layer_set_thumbnails_enabled(GetHandle(), enabled ? 1 : 0) == 0)
        {
            LogFailure(0, "Could not change native thumbnail capture enablement.");
        }
    }

    public void SetWindowVisible(ulong handle, bool visible)
    {
        if (NativeMethods.hypr_layer_set_window_thumbnail_visible(GetHandle(), handle, visible ? 1 : 0) == 0)
        {
            // Removed windows are no longer valid IDs and need no capture cleanup.
            if (!visible && !_windows.Any(window => window.Handle == handle))
            {
                return;
            }

            LogFailure(handle, "Could not change native thumbnail visibility.");
        }
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed)
        {
            return;
        }

        if (_layer.NativeHandle != IntPtr.Zero)
        {
            SetEnabled(false);
        }

        _disposed = true;
        _windows = [];
        _revisions.Clear();
        _errors.Clear();
    }

    private IntPtr GetHandle()
    {
        CheckThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ObjectDisposedException.ThrowIf(_layer.NativeHandle == IntPtr.Zero, _layer);
        return _layer.NativeHandle;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("Window thumbnail backend must be used on its owning main thread.");
        }
    }

    private string ReadString(ulong id, Func<IntPtr, ulong, byte[]?, int, int> getter)
    {
        var native = GetHandle();
        var length = getter(native, id, null, 0);
        if (length <= 1)
        {
            return "";
        }

        var buffer = new byte[length];
        var copied = getter(native, id, buffer, buffer.Length);
        return copied <= 0 || copied > buffer.Length
            ? throw new InvalidOperationException("Could not read native thumbnail metadata.")
            : Encoding.UTF8.GetString(buffer, 0, copied - 1);
    }

    private void LogFailure(ulong id, string fallback)
    {
        var error = ReadString(id, NativeMethods.hypr_layer_get_thumbnail_error);
        LogError(id, string.IsNullOrEmpty(error) ? fallback : error);
    }

    private void LogNativeError(ulong id)
    {
        var error = ReadString(id, NativeMethods.hypr_layer_get_thumbnail_error);
        if (!string.IsNullOrEmpty(error))
        {
            LogError(id, error);
        }
    }

    private void LogError(ulong id, string error)
    {
        if (_errors.TryGetValue(id, out var previous) && previous == error)
        {
            return;
        }

        _errors[id] = error;
        AppLogger.Warning("WindowThumbnails", $"Native thumbnail backend (handle {id}): {error}");
    }
}

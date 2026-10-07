using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

public sealed class WindowThumbnailService(IWindowThumbnailBackend? backend = null) : IWindowThumbnailService
{
    private sealed record Visibility(string OutputName, HashSet<ulong>? Handles);
    private readonly Dictionary<object, Visibility> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, WindowThumbnailSnapshot> _windows = [];
    private readonly HashSet<ulong> _visible = [];
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private IReadOnlyList<WindowThumbnailMetadata>? _metadata;
    private bool _snapshotDirty;
    private bool _enabled;
    private bool _enabledInitialized;
    private bool _disposed;

    public bool IsAvailable => !_disposed && backend?.IsAvailable == true;
    public IReadOnlyList<WindowThumbnailSnapshot> Snapshot { get; private set; } = [];
    public event Action<WindowThumbnailSnapshot>? WindowAdded;
    public event Action<WindowThumbnailSnapshot>? WindowRemoved;

    public void SetOverviewVisible(object owner, string outputName, bool visible, IReadOnlyCollection<ulong>? handles = null)
    {
        CheckThread();
        if (visible)
        {
            _owners[owner] = new(outputName, handles is null ? null : new(handles));
        }
        else
        {
            _owners.Remove(owner);
        }

        ApplyVisibility();
    }

    public void RemoveOwner(object owner)
    {
        CheckThread();
        if (_owners.Remove(owner))
        {
            ApplyVisibility();
        }
    }

    public void Update()
    {
        CheckThread();
        if (backend is null)
        {
            return;
        }

        try
        {

            var polled = backend.PollWindows();
            var removed = Array.Empty<WindowThumbnailSnapshot>();
            var added = new List<WindowThumbnailSnapshot>();
            if (!ReferenceEquals(polled, _metadata))
            {
                var metadata = polled.ToDictionary(window => window.Handle);
                removed = _windows.Values.Where(window => !metadata.ContainsKey(window.Window.Handle)).ToArray();
                foreach (var window in removed)
                {
                    _windows.Remove(window.Window.Handle);
                }

                foreach (var window in metadata.Values)
                {
                    if (_windows.TryGetValue(window.Handle, out var previous))
                    {
                        _windows[window.Handle] = previous with {
                            Window = window
                        };
                    }
                    else
                    {
                        var snapshot = new WindowThumbnailSnapshot(window, null);
                        _windows.Add(window.Handle, snapshot);
                        added.Add(snapshot);
                    }
                }
                _metadata = polled;
                _snapshotDirty = true;
            }
            ApplyVisibility();
            if (_enabled)
            {
                foreach (var frame in backend.PollFrames())
                {
                    if (_visible.Contains(frame.Handle) && _windows.TryGetValue(frame.Handle, out var window) &&
                        (window.Frame is null || frame.Revision > window.Frame.Revision))
                    {
                        _windows[frame.Handle] = window with {
                            Frame = frame
                        };
                        _snapshotDirty = true;
                    }
                }
            }

            PublishSnapshot();
            foreach (var window in removed)
            {
                WindowRemoved?.Invoke(window);
            }

            foreach (var window in added)
            {
                WindowAdded?.Invoke(_windows[window.Window.Handle]);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("WindowThumbnails", "Could not poll window thumbnails; retaining previous frames", exception);
        }
    }

    private void ApplyVisibility()
    {
        if (backend is null)
        {
            return;
        }

        try
        {
            var enabled = backend.IsAvailable && _owners.Count > 0;
            var wanted = enabled
                ? _windows.Keys.Where(handle => _owners.Values.Any(owner => owner.Handles is null || owner.Handles.Contains(handle))).ToHashSet()
                : [];

            foreach (var handle in _visible.Except(wanted).ToArray())
            {
                backend.SetWindowVisible(handle, false);
                _visible.Remove(handle);
                if (_windows.TryGetValue(handle, out var window))
                {
                    _windows[handle] = window with {
                        Frame = null
                    };
                    _snapshotDirty = true;
                }
            }

            foreach (var handle in wanted.Except(_visible).ToArray())
            {
                backend.SetWindowVisible(handle, true);
                _visible.Add(handle);
            }

            if (!_enabledInitialized || enabled != _enabled)
            {
                backend.SetEnabled(enabled);
                _enabled = enabled;
                _enabledInitialized = true;
            }
            PublishSnapshot();
        }
        catch (Exception exception)
        {
            AppLogger.Warning("WindowThumbnails", "Could not update thumbnail capture visibility", exception);
        }
    }

    private void PublishSnapshot()
    {
        if (!_snapshotDirty)
        {
            return;
        }

        Snapshot = Array.AsReadOnly(_windows.Values.OrderBy(window => window.Window.Handle).ToArray());
        _snapshotDirty = false;
    }

    private void CheckThread()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("Window thumbnail service must be used on its owning main thread.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CheckThread();
        _owners.Clear();
        ApplyVisibility();
        _disposed = true;
        try
        {
            backend?.Dispose();
        }
        catch (Exception exception)
        {
            AppLogger.Warning("WindowThumbnails", "Could not dispose thumbnail backend", exception);
        }

        _windows.Clear();
        Snapshot = [];
        WindowAdded = null;
        WindowRemoved = null;
    }
}

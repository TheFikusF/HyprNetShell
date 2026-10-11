using System.Diagnostics;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

public sealed class WindowThumbnailService(IWindowThumbnailBackend? backend = null) : IWindowThumbnailService
{
    private sealed record Visibility(string OutputName, HashSet<ulong>? Handles, Dictionary<ulong, WindowThumbnailSize>? Sizes);
    private const int STARTUP_WARMUP_SECONDS = 8;

    private readonly HashSet<ulong> _startupSatisfied = [];
    private readonly Dictionary<object, Visibility> _owners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ulong, WindowThumbnailSnapshot> _windows = [];
    private readonly HashSet<ulong> _visible = [];
    private readonly Dictionary<ulong, WindowThumbnailSize> _sizes = [];
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private IReadOnlyList<WindowThumbnailMetadata>? _metadata;
    private bool _snapshotDirty;
    private bool _enabled;
    private bool _enabledInitialized;
    private bool _disposed;
    private long? _startupStarted;
    private bool _startupStopped;

    public bool IsAvailable => !_disposed && backend?.IsAvailable == true;
    public IReadOnlyList<WindowThumbnailSnapshot> Snapshot { get; private set; } = [];
    public event Action<WindowThumbnailSnapshot>? WindowAdded;
    public event Action<WindowThumbnailSnapshot>? WindowRemoved;

    public void SetOverviewVisible(object owner, string outputName, bool visible, IReadOnlyCollection<ulong>? handles = null,
        IReadOnlyDictionary<ulong, WindowThumbnailSize>? sizes = null)
    {
        CheckThread();
        if (visible)
        {
            _owners[owner] = new(outputName, handles is null ? null : new(handles), sizes is null ? null : new(sizes));
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

        _startupStarted ??= Stopwatch.GetTimestamp();

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
                    _sizes.Remove(window.Window.Handle);
                    _startupSatisfied.Remove(window.Window.Handle);
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
            PollFrames();
            ApplyVisibility(pollFrames: false);

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

    private void PollFrames()
    {
        if (!_enabled || backend is null)
        {
            return;
        }

        foreach (var frame in backend.PollFrames())
        {
            if (_visible.Contains(frame.Handle) && _windows.TryGetValue(frame.Handle, out var window) &&
                (window.Frame is null || frame.Revision > window.Frame.Revision))
            {
                _windows[frame.Handle] = window with { Frame = frame };
                _startupSatisfied.Add(frame.Handle);
                _snapshotDirty = true;
            }
        }
    }

    private void ApplyVisibility(bool pollFrames = true)
    {
        if (backend is null)
        {
            return;
        }

        try
        {
            // Disabling native capture can discard pending buffers, so drain them first.
            if (pollFrames)
            {
                PollFrames();
            }
            var warming = !_startupStopped && _startupStarted is { } started &&
                Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(STARTUP_WARMUP_SECONDS);
            var wanted = backend.IsAvailable
                ? _windows.Keys.Where(handle =>
                    _owners.Values.Any(owner => owner.Handles is null || owner.Handles.Contains(handle)) ||
                    (warming && !_startupSatisfied.Contains(handle) && _windows[handle].Frame is null)).ToHashSet()
                : [];
            var enabled = wanted.Count > 0;

            foreach (var handle in _visible.Except(wanted).ToArray())
            {
                backend.SetWindowVisible(handle, false);
                _visible.Remove(handle);
                _sizes.Remove(handle);
            }

            foreach (var handle in wanted)
            {
                var width = 0;
                var height = 0;
                foreach (var owner in _owners.Values)
                {
                    if (owner.Handles is not null && !owner.Handles.Contains(handle))
                    {
                        continue;
                    }

                    var size = owner.Sizes?.GetValueOrDefault(handle) ?? WindowThumbnailSize.Default;
                    // A default record struct can bypass the positive-dimension constructor.
                    if (size.Width <= 0 || size.Height <= 0)
                    {
                        size = WindowThumbnailSize.Default;
                    }

                    width = Math.Max(width, size.Width);
                    height = Math.Max(height, size.Height);
                }

                var demand = width > 0 && height > 0
                    ? new WindowThumbnailSize(width, height)
                    : WindowThumbnailSize.Default;
                if (!_sizes.TryGetValue(handle, out var previous) || previous != demand)
                {
                    backend.SetWindowSize(handle, demand.Width, demand.Height);
                    _sizes[handle] = demand;
                }

                if (!_visible.Contains(handle))
                {
                    backend.SetWindowVisible(handle, true);
                    _visible.Add(handle);
                }
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
        _startupStopped = true;
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
        _sizes.Clear();
        _visible.Clear();
        _startupSatisfied.Clear();
        _metadata = null;
        Snapshot = [];
        WindowAdded = null;
        WindowRemoved = null;
    }
}

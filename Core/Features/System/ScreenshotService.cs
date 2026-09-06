using System.Collections.Concurrent;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.System;

public enum ScreenshotMode
{
    Area,
    Full,
    Ocr,
}

internal sealed class ScreenshotService : IDisposable
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly IHyprctl _hyprctl;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentQueue<ScreenshotMode> _requests = new();
    private readonly Task _bindingTask;
    private bool _disposed;

    internal ScreenshotService(IHyprctl hyprctl)
    {
        _hyprctl = hyprctl;
        _bindingTask = Task.Run(() => InstallBindingsAsync(_lifetime.Token));
    }

    internal bool TryTakeRequest(out ScreenshotMode mode) => _requests.TryDequeue(out mode);

    private async Task InstallBindingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await BindAsync("Print", ScreenshotMode.Area, cancellationToken);
            await BindAsync("CTRL + Print", ScreenshotMode.Full, cancellationToken);
            await BindAsync("SHIFT + Print", ScreenshotMode.Ocr, cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Error("Screenshots", "Screenshot key bindings stopped", exception);
        }
    }

    private async Task BindAsync(string keys, ScreenshotMode mode, CancellationToken cancellationToken)
    {
        if (!await _hyprctl.Bind(keys, () => _requests.Enqueue(mode), cancellationToken: cancellationToken))
        {
            AppLogger.Warning("Screenshots", $"Could not bind {keys}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        try
        {
            _bindingTask.Wait(DisposeTimeout);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Screenshots", "Screenshot bindings did not stop cleanly", exception);
        }
        _lifetime.Dispose();
    }
}

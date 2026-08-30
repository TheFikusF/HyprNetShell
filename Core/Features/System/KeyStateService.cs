using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.System;

internal sealed class KeyStateService : IDisposable
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);
    private const string SUPER_DOWN_BIND = "SUPER_L";
    private const string SUPER_UP_BIND = "SUPER + SUPER_L";

    private readonly CancellationTokenSource _cts = new();
    private readonly IHyprctl _hyprctl;
    private readonly Task _runTask;

    private DateTime _lastLoggedSuperDown;
    private int _isHeld;
    private bool _disposed;

    public bool IsHeld => Volatile.Read(ref _isHeld) != 0;

    public bool IsHeldFor(TimeSpan timespan) => IsHeld && DateTime.Now - _lastLoggedSuperDown > timespan;



    public KeyStateService(IHyprctl hyprctl)
    {
        _hyprctl = hyprctl;
        _runTask = Task.Run(() => RunAsync(_cts.Token));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            _runTask.Wait(DisposeTimeout);
        }
        catch (Exception error)
        {
            Log($"unbind failed during dispose: {error.GetType().Name}: {error.Message}");
        }

        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InstallHyprlandBindsAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Log($"service failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private async Task InstallHyprlandBindsAsync(CancellationToken cancellationToken)
    {
        await _hyprctl.Bind(
            SUPER_DOWN_BIND,
            () =>
            {
                SetHeld(true);
                _lastLoggedSuperDown = DateTime.Now;
            },
            new HyprlandBindOptions(Transparent: true),
            cancellationToken);

        await _hyprctl.Bind(
            SUPER_UP_BIND,
            () => SetHeld(false),
            new HyprlandBindOptions(Release: true, Transparent: true),
            cancellationToken);

        Log("installed Hyprland Super press/release binds");
    }

    private void SetHeld(bool held)
    {
        var value = held ? 1 : 0;
        Interlocked.Exchange(ref _isHeld, value);
    }

    private static void Log(string message) => AppLogger.Info("SuperKeyState", message);
}

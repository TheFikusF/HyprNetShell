using System.Diagnostics;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Platform;

internal sealed class UrlLauncher(
    HyprlandService hyprland,
    IHyprctl hyprctl,
    Action closeDialog) : IDisposable
{
    private static readonly TimeSpan FocusTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FocusPollInterval = TimeSpan.FromMilliseconds(75);

    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    internal void Open(string url)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("A valid HTTP or HTTPS URL is required.", nameof(url));
        }

        var baselineWorkspaceId = hyprland.Snapshot.FocusedWorkspaceId;
        var baselineWindows = hyprland.Snapshot.Windows.ToDictionary(
            window => window.Address,
            window => window.Title,
            StringComparer.Ordinal);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        }) ?? throw new InvalidOperationException("Could not open the system browser.");

        closeDialog();

        _ = FocusBrowserAsync(baselineWorkspaceId, baselineWindows, _lifetime.Token);
    }

    internal bool TryOpen(string url)
    {
        try
        {
            Open(url);
            return true;
        }
        catch (Exception exception)
        {
            AppLogger.Error("UrlLauncher", "Failed to open URL", exception);
            return false;
        }
    }

    private async Task FocusBrowserAsync(
        int baselineWorkspaceId,
        IReadOnlyDictionary<string, string> baselineWindows,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(FocusTimeout.TotalSeconds * Stopwatch.Frequency);
        try
        {
            while (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(FocusPollInterval, cancellationToken);
                var snapshot = hyprland.Snapshot;
                if (snapshot.FocusedWorkspaceId != baselineWorkspaceId)
                {
                    return;
                }

                var browser = snapshot.Windows.FirstOrDefault(window =>
                    !baselineWindows.TryGetValue(window.Address, out var previousTitle) ||
                    !string.Equals(window.Title, previousTitle, StringComparison.Ordinal));

                if (browser is not null)
                {
                    await hyprctl.FocusWindowAsync(browser.Address, cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected when the launcher is disposed during shutdown.
        }
        catch (Exception exception)
        {
            AppLogger.Warning("UrlLauncher", "Could not focus the browser window", exception);
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
        _lifetime.Dispose();
    }
}

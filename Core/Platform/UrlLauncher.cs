using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    internal static bool IsWebsite(Uri uri) => uri.Scheme is "http" or "https";

    internal static bool TryParseLink(string text, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        if (text.Length == 0 || text.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            text.Contains('\\') || text.StartsWith('/') || text.StartsWith('?') || text.StartsWith('='))
        {
            return false;
        }

        // A dotted host may include a port, which Uri would otherwise parse as a scheme.
        if (Uri.TryCreate($"https://{text}", UriKind.Absolute, out var website) &&
            website.HostNameType == UriHostNameType.Dns && website.Host.Contains('.') &&
            website.Host.TrimEnd('.').Split('.')[^1].Any(char.IsAsciiLetter) && website.UserInfo.Length == 0 &&
            !text.Contains("://", StringComparison.Ordinal))
        {
            uri = website;
            return true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var link) || link.IsFile ||
            link.Scheme is "javascript" or "data" or "about" ||
            (IsWebsite(link) && link.Host.Length == 0) || text.EndsWith(':'))
        {
            return false;
        }

        uri = link;
        return true;
    }

    internal async Task TryOpenLinkAsync(string url, bool privateWindow, Func<bool> isSearchActive)
    {
        var cancellationToken = _lifetime.Token;
        try
        {
            var uri = new Uri(url, UriKind.Absolute);
            var baselineWorkspaceId = hyprland.Snapshot.FocusedWorkspaceId;
            var baselineWindows = hyprland.Snapshot.Windows.ToDictionary(
                window => window.Address, window => window.Title, StringComparer.Ordinal);

            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Could not locate the desktop launch helper.");
            var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
            startInfo.ArgumentList.Add(privateWindow ? "--launch-private-uri" : "--launch-uri");
            startInfo.ArgumentList.Add(uri.AbsoluteUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the desktop launch helper.");
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0)
            {
                AppLogger.Warning("UrlLauncher", privateWindow && process.ExitCode == 2
                    ? "The default browser does not expose a private-window desktop action."
                    : $"The desktop launch helper could not open the link (exit code {process.ExitCode}). Check the default handler for its scheme.");
                return;
            }

            if (!cancellationToken.IsCancellationRequested && isSearchActive())
            {
                closeDialog();
                if (IsWebsite(uri))
                {
                    _ = FocusBrowserAsync(baselineWorkspaceId, baselineWindows, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            AppLogger.Warning("UrlLauncher", "Timed out waiting for the desktop link handler.");
        }
        catch (Exception exception)
        {
            AppLogger.Warning("UrlLauncher", $"Could not launch the {(privateWindow ? "private browser" : "desktop link handler")} ({exception.GetType().Name}).");
        }
    }


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

        using var process = Process.Start(new ProcessStartInfo {
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

using System.Diagnostics;
using System.Text.RegularExpressions;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed partial class NotificationService : IBarDataService, IDisposable
{
    private static readonly TimeSpan CountRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly List<NotificationSnapshot> _items = [];
    private bool _started;
    private int _count;
    private DateTime _lastCountRefresh = DateTime.MinValue;

    public async ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        StartSubscriptions();
        await RefreshCountAsync(cancellationToken);
        var items = GetItems();
        state.Notifications = new NotificationsSnapshot(Math.Max(_count, items.Count), items);
    }

    public void Dispose()
    {
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private void StartSubscriptions()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = Task.Run(() => RunSwayncSubscriptionAsync(_disposeCts.Token));
        _ = Task.Run(() => RunNotificationMonitorAsync(_disposeCts.Token));
    }

    private IReadOnlyList<NotificationSnapshot> GetItems()
    {
        lock (_gate)
        {
            return _items.ToArray();
        }
    }

    private async Task RefreshCountAsync(CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastCountRefresh < CountRefreshInterval)
        {
            return;
        }

        _lastCountRefresh = DateTime.UtcNow;
        _count = await ReadCountAsync(cancellationToken);
        TrimToCount(_count);
    }

    private static async Task<int> ReadCountAsync(CancellationToken cancellationToken)
    {
        var output = await CommandRunner.TryReadAsync(
            "swaync-client",
            "-c",
            TimeSpan.FromMilliseconds(500),
            cancellationToken);

        return int.TryParse(output?.Trim(), out var count) ? Math.Max(0, count) : 0;
    }

    private void TrimToCount(int count)
    {
        lock (_gate)
        {
            if (count == 0)
            {
                _items.Clear();
            }
            else if (_items.Count > count)
            {
                _items.RemoveRange(count, _items.Count - count);
            }
        }
    }

    private async Task RunSwayncSubscriptionAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                process = StartProcess("swaync-client", ["-s"]);
                if (process is not null)
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await DelayRestartAsync(cancellationToken);
            }
            finally
            {
                TryKill(process);
                process?.Dispose();
            }
        }
    }

    private async Task RunNotificationMonitorAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                process = StartProcess(
                    "dbus-monitor",
                    ["--session", "interface='org.freedesktop.Notifications',member='Notify'"]);
                if (process is not null)
                {
                    await ReadNotificationMonitorAsync(process, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await DelayRestartAsync(cancellationToken);
            }
            finally
            {
                TryKill(process);
                process?.Dispose();
            }
        }
    }

    private async Task ReadNotificationMonitorAsync(Process process, CancellationToken cancellationToken)
    {
        var parser = new NotifyCallParser();

        while (!process.StandardOutput.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                continue;
            }

            if (line.Contains("member=Notify", StringComparison.Ordinal))
            {
                parser.Reset();
                continue;
            }

            if (parser.TryRead(line, out var notification))
            {
                AddNotification(notification.AppName, notification.Title, notification.Body, notification.IconName);
            }
        }
    }

    private void AddNotification(string appName, string title, string body, string iconName)
    {
        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        lock (_gate)
        {
            _items.Insert(0, new NotificationSnapshot(
                string.IsNullOrWhiteSpace(title) ? appName : title,
                body,
                appName,
                iconName));
            if (_items.Count > 12)
            {
                _items.RemoveRange(12, _items.Count - 12);
            }
        }
    }

    private static Process? StartProcess(string fileName, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo);
    }

    private static async Task DelayRestartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RestartDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(true);
            }
        }
        catch
        {
        }
    }

    private static string UnescapeDbusString(string value) =>
        value
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\n", " ", StringComparison.Ordinal);

    [GeneratedRegex("^\\s+string \"(.*)\"")]
    private static partial Regex DbusStringRegex();

    private sealed class NotifyCallParser
    {
        private readonly List<string> _strings = [];
        private bool _reading;
        private bool _skipNextString;

        public void Reset()
        {
            _strings.Clear();
            _reading = true;
            _skipNextString = false;
        }

        public bool TryRead(string line, out NotificationSnapshot notification)
        {
            notification = new NotificationSnapshot("", "", "");
            if (!_reading)
            {
                return false;
            }

            if (line.Contains("array [", StringComparison.Ordinal) ||
                line.Contains("dict entry(", StringComparison.Ordinal))
            {
                _reading = false;
                return TryBuild(out notification);
            }

            if (line.Contains("variant", StringComparison.Ordinal))
            {
                _skipNextString = true;
                return false;
            }

            var match = DbusStringRegex().Match(line);
            if (!match.Success)
            {
                return false;
            }

            if (_skipNextString)
            {
                _skipNextString = false;
                return false;
            }

            _strings.Add(UnescapeDbusString(match.Groups[1].Value));
            if (_strings.Count < 4)
            {
                return false;
            }

            _reading = false;
            return TryBuild(out notification);
        }

        private bool TryBuild(out NotificationSnapshot notification)
        {
            notification = new NotificationSnapshot("", "", "");
            if (_strings.Count < 4)
            {
                return false;
            }

            notification = new NotificationSnapshot(_strings[2], _strings[3], _strings[0], _strings[1]);
            return true;
        }
    }
}

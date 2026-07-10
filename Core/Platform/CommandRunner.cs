using System.Diagnostics;

namespace HyprNetShell.Core.Platform;

internal static class CommandRunner
{
    public static async Task<string?> TryReadAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            process = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            return process.ExitCode == 0 ? await outputTask : null;
        }
        catch
        {
            TryKill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    public static async Task TryRunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
        }
        catch
        {
            TryKill(process);
        }
        finally
        {
            process?.Dispose();
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
            // Ignore cleanup failures from already-exited or inaccessible processes.
        }
    }
}

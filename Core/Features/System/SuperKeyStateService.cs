using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using HyprNetShell.Core.Features.Hyprland;

namespace HyprNetShell.Core.Features.System;

internal sealed class SuperKeyStateService : IDisposable
{
    private static readonly TimeSpan HyprctlTimeout = TimeSpan.FromSeconds(2);
    private const string SUPER_DOWN_BIND = "SUPER_L";
    private const string SUPER_UP_BIND = "SUPER + SUPER_L";

    private readonly CancellationTokenSource _cts = new();
    private readonly string _socketPath;
    private readonly Task _runTask;

    private DateTime _lastLoggedSuperDown;
    private Socket? _listener;
    private int _isHeld;
    private bool _disposed;

    public bool IsHeld => Volatile.Read(ref _isHeld) != 0;

    public bool IsHeldFor(TimeSpan timespan) => IsHeld && DateTime.Now - _lastLoggedSuperDown > timespan;

    public SuperKeyStateService()
    {
        _socketPath = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp",
            "hypr-shell.sock");
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
        Log("disposing");

        try
        {
            _listener?.Dispose();
        }
        catch
        {
        }

        try
        {
            UninstallHyprlandBindsAsync(CancellationToken.None).Wait(HyprctlTimeout);
        }
        catch (Exception error)
        {
            Log($"unbind failed during dispose: {error.GetType().Name}: {error.Message}");
        }

        TryDeleteSocket();
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            StartSocket();
            await InstallHyprlandBindsAsync(cancellationToken);
            await AcceptLoopAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Log($"service failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private void StartSocket()
    {
        TryDeleteSocket();

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
        _listener.Listen(8);
        Log($"listening on {_socketPath}");
    }

    private async Task InstallHyprlandBindsAsync(CancellationToken cancellationToken)
    {
        await UninstallHyprlandBindsAsync(cancellationToken);

        await HyprlandService.HyprctlEvalAsync($$"""
                                                 hl.bind("{{SUPER_DOWN_BIND}}",
                                                   hl.dsp.exec_cmd("printf 'super_down\n' | socat - UNIX-CONNECT:{{ShellQuote(_socketPath)}}"),
                                                   { transparent = true })
                                                 """, cancellationToken);

        await HyprlandService.HyprctlEvalAsync($$"""
                                                 hl.bind("{{SUPER_UP_BIND}}",
                                                   hl.dsp.exec_cmd("printf 'super_up\n' | socat - UNIX-CONNECT:{{ShellQuote(_socketPath)}}"),
                                                   { release = true, transparent = true })
                                                 """, cancellationToken);

        Log("installed Hyprland Super press/release binds");
    }

    private static async Task UninstallHyprlandBindsAsync(CancellationToken cancellationToken)
    {
        await HyprlandService.HyprctlEvalAsync($$"""hl.unbind("{{SUPER_DOWN_BIND}}")""", cancellationToken);
        await HyprlandService.HyprctlEvalAsync($$"""hl.unbind("{{SUPER_UP_BIND}}")""", cancellationToken);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            var client = await _listener.AcceptAsync(cancellationToken);
            _ = Task.Run(() => ReadClientAsync(client, cancellationToken), cancellationToken);
        }
    }

    private async Task ReadClientAsync(Socket client, CancellationToken cancellationToken)
    {
        try
        {
            using (client)
            {
                var buffer = new byte[128];
                var read = await client.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                if (read <= 0)
                {
                    return;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                HandleMessage(message);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Log($"socket client failed: {error.GetType().Name}: {error.Message}");
        }
    }

    private void HandleMessage(string message)
    {
        switch (message)
        {
            case "super_down":
                SetHeld(true, message);
                _lastLoggedSuperDown = DateTime.Now;
                break;
            case "super_up":
                SetHeld(false, message);
                break;
            default:
                Log($"ignored socket message: '{message}'");
                break;
        }
    }

    private void SetHeld(bool held, string message)
    {
        var value = held ? 1 : 0;
        var previous = Interlocked.Exchange(ref _isHeld, value) != 0;
        Log($"received {message}; held={held}");

        if (previous != held)
        {
            Log($"state changed: held={held}");
        }
    }

    private static string ShellQuote(string value) => $"'{value.Replace("'", "'\\''")}'";

    private void TryDeleteSocket()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
                Log($"deleted stale socket {_socketPath}");
            }
        }
        catch (Exception error)
        {
            Log($"failed to delete socket {_socketPath}: {error.GetType().Name}: {error.Message}");
        }
    }

    private static void Log(string message)
    {
        Console.Error.WriteLine($"[SuperKeyState] {DateTime.Now:HH:mm:ss.fff} {message}");
    }
}
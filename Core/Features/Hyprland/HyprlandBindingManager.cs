using System.Net.Sockets;
using System.Text;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.Hyprland;

internal sealed class HyprlandBindingManager : IDisposable
{
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string, string, HyprlandBindOptions, CancellationToken, Task<bool>> _bindCommand;
    private readonly Func<string, CancellationToken, Task<bool>> _unbind;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly Lock _bindingsLock = new();
    private readonly Dictionary<string, Binding> _bindings = [];
    private readonly string _socketPath;

    private Socket? _listener;
    private Task? _acceptTask;
    private bool _disposed;

    public HyprlandBindingManager(
        Func<string, string, HyprlandBindOptions, CancellationToken, Task<bool>> bindCommand,
        Func<string, CancellationToken, Task<bool>> unbind)
    {
        _bindCommand = bindCommand;
        _unbind = unbind;
        _socketPath = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp",
            $"hyprnetshell-{Environment.ProcessId}-{Guid.NewGuid():N}.sock");
    }

    public async Task<bool> BindAsync(
        string keys,
        Action callback,
        HyprlandBindOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keys);
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        await EnsureStartedAsync(cancellationToken);

        var id = Guid.NewGuid().ToString("N");
        var binding = new Binding(keys, callback);
        lock (_bindingsLock)
        {
            if (_bindings.Values.Any(existing => string.Equals(existing.Keys, keys, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A callback is already bound to '{keys}'.");
            }

            _bindings.Add(id, binding);
        }

        binding.CancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var (manager, bindingId) = ((HyprlandBindingManager, string))state!;
                _ = manager.RemoveBindingAsync(bindingId, unbind: true);
            },
            (this, id));

        var command = $"printf '{id}\\n' | socat - UNIX-CONNECT:{ShellQuote(_socketPath)}";
        if (await _bindCommand(keys, command, options, cancellationToken))
        {
            return true;
        }

        await RemoveBindingAsync(id, unbind: false);
        return false;
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_listener is not null)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_listener is not null)
            {
                return;
            }

            TryDeleteSocket();
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
            listener.Listen(16);
            _listener = listener;
            _acceptTask = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await _listener!.AcceptAsync(cancellationToken);
                _ = ReadClientAsync(client, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Error("HyprlandBindings", "The binding event socket stopped", exception);
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

                var id = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                Action? callback;
                lock (_bindingsLock)
                {
                    callback = _bindings.GetValueOrDefault(id)?.Callback;
                }

                if (callback is not null)
                {
                    _ = Task.Run(() => InvokeCallback(callback), CancellationToken.None);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning("HyprlandBindings", "Could not read a binding event", exception);
        }
    }

    private static void InvokeCallback(Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception exception)
        {
            AppLogger.Error("HyprlandBindings", "A binding callback failed", exception);
        }
    }

    private async Task RemoveBindingAsync(string id, bool unbind)
    {
        Binding? binding;
        lock (_bindingsLock)
        {
            if (!_bindings.Remove(id, out binding))
            {
                return;
            }
        }

        binding.CancellationRegistration.Unregister();
        if (unbind)
        {
            await _unbind(binding.Keys, CancellationToken.None);
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
        _listener?.Dispose();

        Binding[] bindings;
        lock (_bindingsLock)
        {
            bindings = [.._bindings.Values];
            _bindings.Clear();
        }

        foreach (var binding in bindings)
        {
            binding.CancellationRegistration.Unregister();
        }

        try
        {
            Task.WhenAll(bindings.Select(binding => _unbind(binding.Keys, CancellationToken.None)))
                .Wait(DisposeTimeout);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("HyprlandBindings", "Could not remove every binding during shutdown", exception);
        }

        TryDeleteSocket();
        _startLock.Dispose();
        _lifetime.Dispose();
    }

    private void TryDeleteSocket()
    {
        try
        {
            if (File.Exists(_socketPath))
            {
                File.Delete(_socketPath);
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("HyprlandBindings", $"Could not delete event socket {_socketPath}", exception);
        }
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed class Binding(string keys, Action callback)
    {
        public string Keys { get; } = keys;
        public Action Callback { get; } = callback;
        public CancellationTokenRegistration CancellationRegistration { get; set; }
    }
}

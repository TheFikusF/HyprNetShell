namespace HyprNetShell.Core.Features.System;

internal sealed class AuthenticatorCodeCopy : IDisposable
{
    private sealed class AccountState
    {
        internal bool Busy;
        internal string? Code;
        internal long Created;
        internal ITimer? Timer;
    }

    private static readonly TimeSpan RETENTION = TimeSpan.FromMinutes(5);
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AccountState> _accounts = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private Func<string, Task<string?>>? _generate;
    private Func<string, Task<bool>>? _copy;
    private bool _disposed;

    public AuthenticatorCodeCopy(Func<string, Task<string?>> generate, Func<string, Task<bool>> copy, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(generate);
        ArgumentNullException.ThrowIfNull(copy);
        _generate = generate;
        _copy = copy;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsBusy(string id)
    {
        lock (_gate)
        {
            return !_disposed && _accounts.TryGetValue(id, out var state) && state.Busy;
        }
    }

    public bool CanRetry(string id)
    {
        lock (_gate)
        {
            return !_disposed && _accounts.TryGetValue(id, out var state) && !state.Busy && HasCode(state);
        }
    }

    public Task<bool> GenerateAndCopyAsync(string id) => RunAsync(id, false);

    public Task<bool> RetryCopyAsync(string id) => RunAsync(id, true);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _generate = null;
            _copy = null;
            foreach (var state in _accounts.Values)
            {
                ClearCode(state);
            }

            _accounts.Clear();
        }
    }

    private async Task<bool> RunAsync(string id, bool retry)
    {
        AccountState state;
        Func<string, Task<string?>>? generate;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (!_accounts.TryGetValue(id, out state!))
            {
                state = new AccountState();
                _accounts.Add(id, state);
            }

            if (state.Busy || HasCode(state) != retry)
            {
                return false;
            }

            state.Busy = true;
            generate = retry ? null : _generate;
        }

        try
        {
            if (!retry)
            {
                var generated = await generate!(id).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || string.IsNullOrEmpty(generated))
                    {
                        return false;
                    }

                    state.Code = generated;
                    state.Created = _timeProvider.GetTimestamp();
                    state.Timer = _timeProvider.CreateTimer(_ => Expire(id, state), null, RETENTION, Timeout.InfiniteTimeSpan);
                }
            }

            Task<bool> copying;
            lock (_gate)
            {
                if (_disposed || !HasCode(state))
                {
                    return false;
                }

                // Start under the lock so disposal cannot admit a new clipboard action.
                copying = _copy!(state.Code!);
            }

            var copied = await copying.ConfigureAwait(false);
            lock (_gate)
            {
                if (copied)
                {
                    ClearCode(state);
                }

                return !_disposed && copied;
            }
        }
        finally
        {
            lock (_gate)
            {
                state.Busy = false;
                if (!HasCode(state))
                {
                    _accounts.Remove(id);
                }
            }
        }
    }

    private bool HasCode(AccountState state)
    {
        if (state.Code is not null && _timeProvider.GetElapsedTime(state.Created) >= RETENTION)
        {
            ClearCode(state);
        }

        return state.Code is not null;
    }

    private void Expire(string id, AccountState state)
    {
        lock (_gate)
        {
            if (HasCode(state))
            {
                return;
            }

            if (!state.Busy && _accounts.TryGetValue(id, out var current) && ReferenceEquals(current, state))
            {
                _accounts.Remove(id);
            }
        }
    }

    private static void ClearCode(AccountState state)
    {
        state.Code = null;
        state.Timer?.Dispose();
        state.Timer = null;
    }
}

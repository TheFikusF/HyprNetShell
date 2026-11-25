using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace HyprNetShell.Authorization;

public sealed record AuthorizationPrompt(ulong Id, string Message, int PasswordLength);

public sealed class DesktopAuthorization : IDisposable
{
    private sealed record ChargeRequest(ChargeLimitSession Session, nint Handle, CancellationToken Token)
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Submitted { get; set; }
        public bool FreshCredential { get; set; }
    }


    private const int PASSWORD_CAPACITY = 256;
    private const int POLL_INTERVAL_MS = 25;
    private const int LEASE_SECONDS = 60;

    private readonly Lock _gate = new();
    private readonly byte[] _edit = GC.AllocateArray<byte>(PASSWORD_CAPACITY, pinned: true);
    private readonly byte[] _chargeCredential = GC.AllocateArray<byte>(PASSWORD_CAPACITY, pinned: true);
    private readonly byte[] _polkitMessage = new byte[8192];
    private readonly Action<string> _logWarning;
    private readonly Timer _timer;
    private AuthorizationPrompt? _prompt;
    private ChargeRequest? _request;
    private ChargeLimitSession? _leaseOwner;
    private long _leaseStart;
    private ulong _nextId;
    private ulong _polkitGeneration;
    private ulong _epoch;
    private nint _polkit;
    private int _editBytes;
    private bool _polkitStartAttempted;
    private bool _disposed;

    public AuthorizationPrompt? Prompt
    {
        get
        {
            lock (_gate)
            {
                return _prompt;
            }
        }
    }

    public DesktopAuthorization(Action<string> logWarning)
    {
        ArgumentNullException.ThrowIfNull(logWarning);
        _logWarning = logWarning;
        _timer = new Timer(Tick);
        StartPolkit();
    }

    public void Update()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            StartPolkit();
            if (_polkit == 0 || (_prompt is not null && _polkitGeneration == 0))
            {
                return;
            }

            var generation = AuthorizationNative.PollPolkit(_polkit, ref _polkitMessage[0], (nuint)_polkitMessage.Length);
            if (generation == _polkitGeneration)
            {
                return;
            }

            ClearPrompt();
            if (generation != 0)
            {
                _polkitGeneration = generation;
                ShowPrompt(Encoding.UTF8.GetString(_polkitMessage.AsSpan(0, Array.IndexOf(_polkitMessage, (byte)0))));
            }
        }
    }

    public void Append(ulong id, ReadOnlySpan<char> text)
    {
        lock (_gate)
        {
            if (_prompt is not { } prompt || prompt.Id != id)
            {
                return;
            }

            var start = _editBytes;
            var length = prompt.PasswordLength;
            while (!text.IsEmpty)
            {
                if (Rune.DecodeFromUtf16(text, out var rune, out var consumed) != OperationStatus.Done ||
                    rune.Value < 0x20 || rune.Value == 0x7f || _editBytes + rune.Utf8SequenceLength >= _edit.Length)
                {
                    CryptographicOperations.ZeroMemory(_edit.AsSpan(start, _editBytes - start));
                    _editBytes = start;
                    return;
                }

                _editBytes += rune.EncodeToUtf8(_edit.AsSpan(_editBytes));
                length++;
                text = text[consumed..];
            }

            _prompt = prompt with { PasswordLength = length };
        }
    }

    public void Backspace(ulong id)
    {
        lock (_gate)
        {
            if (_prompt is not { } prompt || prompt.Id != id || _editBytes == 0)
            {
                return;
            }

            var end = _editBytes;
            do
            {
                _editBytes--;
            }
            while ((_edit[_editBytes] & 0xc0) == 0x80);

            CryptographicOperations.ZeroMemory(_edit.AsSpan(_editBytes, end - _editBytes));
            _prompt = prompt with { PasswordLength = prompt.PasswordLength - 1 };
        }
    }

    public void Submit(ulong id)
    {
        lock (_gate)
        {
            if (_prompt?.Id != id)
            {
                return;
            }

            if (_polkitGeneration != 0)
            {
                try
                {
                    AuthorizationNative.AnswerPolkit(_polkit, _polkitGeneration, ref _edit[0]);
                }
                finally
                {
                    ClearPrompt();
                }

                return;
            }

            if (_request is not { } request || request.Token.IsCancellationRequested)
            {
                FinishCharge(false);
                return;
            }

            ClearLease();
            _edit.AsSpan(0, _editBytes).CopyTo(_chargeCredential);
            ClearPrompt();
            request.FreshCredential = true;
            SendChargeCredential(request);
        }
    }

    public void Cancel(ulong id)
    {
        lock (_gate)
        {
            if (_prompt?.Id != id)
            {
                return;
            }

            if (_polkitGeneration != 0)
            {
                try
                {
                    AuthorizationNative.AnswerPolkit(_polkit, _polkitGeneration, ref Unsafe.NullRef<byte>());
                }
                finally
                {
                    ClearPrompt();
                }
            }
            else
            {
                FinishCharge(false);
            }
        }
    }

    public void RevokeAll()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                Revoke();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                Revoke();
            }
            finally
            {
                _timer.Dispose();
            }
        }
    }

    internal ChargeLimitSession BeginChargeLimitSession()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new ChargeLimitSession(this, _epoch);
        }
    }

    internal Task<bool> SetLimitAsync(ChargeLimitSession session, int percent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<bool>(cancellationToken);
            }

            if (percent is < 60 or > 100 || _disposed || session.IsDisposed || session.Epoch != _epoch || _request is not null)
            {
                return Task.FromResult(false);
            }

            ExpireLease();
            nint handle;
            try
            {
                var command = FormattableString.Invariant($"printf '%s\\n' {percent} > /sys/class/power_supply/BAT0/charge_control_end_threshold");
                handle = AuthorizationNative.StartSu(command);
            }
            catch (Exception exception) when (IsInteropUnavailable(exception))
            {
                _logWarning("Cannot start charge-limit authorization: the native su ABI is unavailable.");
                return Task.FromResult(false);
            }

            if (handle == 0)
            {
                _logWarning("Cannot start charge-limit authorization with /usr/bin/su.");
                return Task.FromResult(false);
            }

            _request = new ChargeRequest(session, handle, cancellationToken);
            _timer.Change(0, POLL_INTERVAL_MS);
            return _request.Completion.Task;
        }
    }

    internal void EndSession(ChargeLimitSession session)
    {
        lock (_gate)
        {
            session.IsDisposed = true;
            if (_leaseOwner == session)
            {
                ClearLease();
            }

            if (_request?.Session == session)
            {
                FinishCharge(false);
            }
        }
    }

    private void StartPolkit()
    {
        if (_polkitStartAttempted)
        {
            return;
        }

        _polkitStartAttempted = true;
        try
        {
            _polkit = AuthorizationNative.StartPolkit();
            if (_polkit == 0)
            {
                _logWarning("Cannot start the native polkit agent.");
            }
        }
        catch (Exception exception) when (IsInteropUnavailable(exception))
        {
            _logWarning("Cannot start the polkit agent: the native polkit ABI is unavailable.");
        }
    }

    private static bool IsInteropUnavailable(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private void Tick(object? state)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            ExpireLease();
            if (_request is { } request)
            {
                PollCharge(request);
            }

            if (_request is null && _leaseOwner is null)
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }
    }

    private void PollCharge(ChargeRequest request)
    {
        if (request.Token.IsCancellationRequested)
        {
            FinishCharge(false);
            return;
        }

        switch (AuthorizationNative.PollSu(request.Handle))
        {
            case 0:
                return;
            case 1 when !request.Submitted:
                ExpireLease();
                if (_leaseOwner == request.Session)
                {
                    SendChargeCredential(request);
                }
                else if (_prompt is null)
                {
                    ShowPrompt("Enter the root password to change the BAT0 charge limit.");
                }

                return;
            case 2:
                FinishCharge(true);
                return;
            default:
                _logWarning("Charge-limit authorization or BAT0 threshold write failed or timed out.");
                FinishCharge(false);
                return;
        }
    }

    private void SendChargeCredential(ChargeRequest request)
    {
        if (request.Token.IsCancellationRequested)
        {
            FinishCharge(false);
            return;
        }

        request.Submitted = AuthorizationNative.AnswerSu(request.Handle, ref _chargeCredential[0]) == 1;
        if (!request.Submitted)
        {
            _logWarning("The native su process did not accept the charge-limit response.");
            FinishCharge(false);
        }
    }

    private void FinishCharge(bool success)
    {
        if (_polkitGeneration == 0)
        {
            ClearPrompt();
        }

        if (_request is not { } request)
        {
            return;
        }

        var canceled = request.Token.IsCancellationRequested;
        // Replaying the lease or succeeding without a password never renews it.
        if (!canceled && success && request.FreshCredential && request.Submitted)
        {
            _leaseStart = Stopwatch.GetTimestamp();
            _leaseOwner = request.Session;
        }
        else if ((!success || canceled) && (request.FreshCredential || _leaseOwner == request.Session))
        {
            ClearLease();
        }

        _request = null;
        AuthorizationNative.DestroySu(request.Handle);
        if (canceled)
        {
            request.Completion.TrySetCanceled(request.Token);
        }
        else
        {
            request.Completion.TrySetResult(success);
        }
    }

    private void ExpireLease()
    {
        if (_leaseOwner is not null && Stopwatch.GetElapsedTime(_leaseStart).TotalSeconds >= LEASE_SECONDS)
        {
            ClearLease();
        }
    }

    private void ClearLease()
    {
        CryptographicOperations.ZeroMemory(_chargeCredential);
        _leaseOwner = null;
        _leaseStart = 0;
    }

    private void ShowPrompt(string message)
    {
        _prompt = new AuthorizationPrompt(++_nextId, message, 0);
    }

    private void ClearPrompt()
    {
        CryptographicOperations.ZeroMemory(_edit);
        _editBytes = 0;
        _prompt = null;
        _polkitGeneration = 0;
    }

    private void Revoke()
    {
        _epoch++;
        ClearPrompt();
        ClearLease();
        try
        {
            FinishCharge(false);
        }
        finally
        {
            if (_polkit != 0)
            {
                var handle = _polkit;
                _polkit = 0;
                AuthorizationNative.DestroyPolkit(handle);
            }

            _polkitStartAttempted = false;
        }
    }
}

internal sealed class ChargeLimitSession : IDisposable
{
    private readonly DesktopAuthorization _owner;

    internal ulong Epoch { get; }
    internal bool IsDisposed { get; set; }

    internal ChargeLimitSession(DesktopAuthorization owner, ulong epoch)
    {
        _owner = owner;
        Epoch = epoch;
    }

    public Task<bool> SetLimitAsync(int percent, CancellationToken cancellationToken = default) =>
        _owner.SetLimitAsync(this, percent, cancellationToken);

    public void Dispose() => _owner.EndSession(this);
}

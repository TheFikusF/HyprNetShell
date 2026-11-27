using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace HyprNetShell.Native.Interop.Authorization;

public sealed record AuthorizationPrompt(ulong Id, string Message, int PasswordLength);

public sealed class DesktopAuthorization : IDisposable
{
    private abstract record SuRequest(nint Handle, CancellationToken Token)
    {
        public bool Submitted { get; set; }
    }

    private sealed record ChargeRequest(ChargeLimitSession Session, nint Handle, CancellationToken Token) : SuRequest(Handle, Token)
    {
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FreshCredential { get; set; }
    }

    private sealed record SmartRequest(nint Handle, CancellationToken Token) : SuRequest(Handle, Token)
    {
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private const int PASSWORD_CAPACITY = 256;
    private const int POLL_INTERVAL_MS = 25;
    private const int LEASE_SECONDS = 60;
    private const int SMART_OUTPUT_CAPACITY = 1024 * 1024;
    private const int MAX_SMART_DEVICES = 16;

    private readonly Lock _gate = new();
    private readonly byte[] _edit = GC.AllocateArray<byte>(PASSWORD_CAPACITY, pinned: true);
    private readonly byte[] _chargeCredential = GC.AllocateArray<byte>(PASSWORD_CAPACITY, pinned: true);
    private readonly byte[] _polkitMessage = new byte[8192];
    private readonly Action<string> _logWarning;
    private readonly Timer _timer;
    private AuthorizationPrompt? _prompt;
    private SuRequest? _request;
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

            if (request is SmartRequest)
            {
                try
                {
                    request.Submitted = AuthorizationNative.AnswerSu(request.Handle, ref _edit[0]) == 1;
                }
                finally
                {
                    ClearPrompt();
                }

                if (!request.Submitted)
                {
                    _logWarning("The native su process did not accept the SMART authorization response.");
                    FinishCharge(false);
                }

                return;
            }

            ClearLease();
            _edit.AsSpan(0, _editBytes).CopyTo(_chargeCredential);
            ClearPrompt();
            var charge = (ChargeRequest)request;
            charge.FreshCredential = true;
            SendChargeCredential(charge);
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

            var request = new ChargeRequest(session, handle, cancellationToken);
            _request = request;
            _timer.Change(0, POLL_INTERVAL_MS);
            return request.Completion.Task;
        }
    }

    internal Task<string?> ReadSmartAsync(IReadOnlyList<string> devices, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<string?>(cancellationToken);
            }

            if (_disposed || _request is not null || devices.Count is < 1 or > MAX_SMART_DEVICES || devices.Any(device => !IsSmartDevicePath(device)))
            {
                _logWarning("Cannot start SMART authorization: another request is active or the device list is invalid.");
                return Task.FromResult<string?>(null);
            }

            try
            {
                // Only validated device names enter the shell command. Keep each JSON document and its
                // smartctl exit status separate without treating an unhealthy drive as a su failure.
                var command = string.Join("; ", devices.Select(device =>
                    $"/usr/bin/smartctl -j -i -H -A -l devstat -n standby,99 -- {device} 2>/dev/null; printf '\\036%d\\036' \"$?\""));
                var handle = AuthorizationNative.StartSuCapture(command);
                if (handle != 0)
                {
                    var request = new SmartRequest(handle, cancellationToken);
                    _request = request;
                    _timer.Change(0, POLL_INTERVAL_MS);
                    return request.Completion.Task;
                }
            }
            catch (Exception exception) when (IsInteropUnavailable(exception))
            {
                _logWarning("Cannot start SMART authorization: the native su capture ABI is unavailable.");
                return Task.FromResult<string?>(null);
            }

            _logWarning("Cannot start SMART authorization with /usr/bin/su.");
            return Task.FromResult<string?>(null);
        }
    }

    private static bool IsSmartDevicePath(string device) =>
        device.StartsWith("/dev/", StringComparison.Ordinal) && device.Length is > 5 and <= 80 &&
        device[5..].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    internal void EndSession(ChargeLimitSession session)
    {
        lock (_gate)
        {
            session.IsDisposed = true;
            if (_leaseOwner == session)
            {
                ClearLease();
            }

            if (_request is ChargeRequest charge && charge.Session == session)
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

    private void PollCharge(SuRequest request)
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
                if (request is ChargeRequest charge && _leaseOwner == charge.Session)
                {
                    SendChargeCredential(charge);
                }
                else if (_prompt is null)
                {
                    ShowPrompt(request is SmartRequest
                                            ? "Enter the root password to read drive SMART diagnostics."
                                            : "Enter the root password to change the BAT0 charge limit.");
                }

                return;
            case 2:
                FinishCharge(true);
                return;
            default:
                _logWarning(request is SmartRequest
                                    ? "SMART authorization or diagnostics failed or timed out."
                                    : "Charge-limit authorization or BAT0 threshold write failed or timed out.");
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
        if (request is SmartRequest smart)
        {
            var output = smart.Completion;
            string? text = null;
            try
            {
                if (success && !canceled)
                {
                    var buffer = new byte[SMART_OUTPUT_CAPACITY];
                    var length = AuthorizationNative.CopySuOutput(request.Handle, ref buffer[0], (nuint)buffer.Length);
                    if (length > 0)
                    {
                        text = Encoding.UTF8.GetString(buffer, 0, length);
                    }
                    else
                    {
                        _logWarning("Could not read captured SMART diagnostics from su.");
                    }
                }
            }
            finally
            {
                _request = null;
                AuthorizationNative.DestroySu(request.Handle);
                if (canceled)
                {
                    output.TrySetCanceled(request.Token);
                }
                else
                {
                    output.TrySetResult(text);
                }
            }

            return;
        }

        var charge = (ChargeRequest)request;
        // Replaying the lease or succeeding without a password never renews it.
        if (!canceled && success && charge.FreshCredential && charge.Submitted)
        {
            _leaseStart = Stopwatch.GetTimestamp();
            _leaseOwner = charge.Session;
        }
        else if ((!success || canceled) && (charge.FreshCredential || _leaseOwner == charge.Session))
        {
            ClearLease();
        }

        _request = null;
        AuthorizationNative.DestroySu(charge.Handle);
        if (canceled)
        {
            charge.Completion.TrySetCanceled(charge.Token);
        }
        else
        {
            charge.Completion.TrySetResult(success);
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

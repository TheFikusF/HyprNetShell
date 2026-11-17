using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using HyprNetShell.Core.Features.OnlineAccounts;
using HyprNetShell.Core.Logging;
using OtpNet;
using FormatException = HyprNetShell.Core.Features.System.AuthenticatorValidationException;

namespace HyprNetShell.Core.Features.System;

internal sealed class AuthenticatorValidationException(string message) : Exception(message);

public sealed record AuthenticatorAccountSnapshot(string Id, string Issuer, string Account, string Code, int Seconds, string Type = "totp");

internal sealed record AuthenticatorStoredAccount(string Id, string Issuer, string Account, byte[] Secret, string Algorithm, int Digits, int Period, string Type = "totp", long Counter = 0);

[JsonSerializable(typeof(AuthenticatorStoredAccount[]))]
internal sealed partial class AuthenticatorJsonContext : JsonSerializerContext;

public sealed class AuthenticatorService : IDisposable
{
    private const string PROVIDER = "authenticator";
    private const int MAX_ACCOUNTS = 256;
    private const int MAX_URI_LENGTH = 8192;
    private const int MAX_PERIOD = 86400;
    private readonly Lock _sync = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ICredentialStore _store;
    private AuthenticatorStoredAccount[] _accounts = [];
    private string _status = "Not loaded.";
    private int _pending;
    private bool _loaded;
    private bool _disposed;

    public IReadOnlyList<AuthenticatorAccountSnapshot> Accounts
    {
        get
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var now = DateTime.UtcNow;
                var unix = new DateTimeOffset(now).ToUnixTimeSeconds();
                return Array.AsReadOnly(_accounts.Select(account => new AuthenticatorAccountSnapshot(
                    account.Id, account.Issuer, account.Account,
                    account.Type == "totp" ? new Totp(account.Secret, account.Period, ParseAlgorithm(account.Algorithm), account.Digits).ComputeTotp(now) : "",
                    account.Type == "totp" ? account.Period - (int)(unix % account.Period) : 0, account.Type)).ToArray());
            }
        }
    }

    public string Status
    {
        get
        {
            lock (_sync)
            {
                return _status;
            }
        }
    }

    public bool IsBusy => Volatile.Read(ref _pending) > 0;

    public AuthenticatorService() : this(new SecretServiceCredentialStore()) { }

    internal AuthenticatorService(ICredentialStore store)
    {
        _store = store;
    }

    public Task LoadAsync() => RunAsync("load", async token =>
    {
        if (_loaded)
        {
            return;
        }

        var json = await _store.ReadAsync(PROVIDER, token);
        AuthenticatorStoredAccount[] accounts = [];
        try
        {
            if (json is not null)
            {
                if (json.Length > 1024 * 1024)
                {
                    throw new FormatException("Saved authenticator data is too large. Restore a valid keyring backup.");
                }

                accounts = JsonSerializer.Deserialize(json, AuthenticatorJsonContext.Default.AuthenticatorStoredAccountArray)
                    ?? throw new FormatException("Saved authenticator data is invalid. Restore a valid keyring backup.");
                ValidateStored(accounts);
            }

            lock (_sync)
            {
                token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                _accounts = accounts;
                _loaded = true;
                accounts = [];
            }
        }
        finally
        {
            Clear(accounts);
        }
    });

    public Task ImportAsync(string uri) => RunAsync("import", async token =>
    {
        EnsureLoaded();
        var added = uri.StartsWith("otpauth-migration:", StringComparison.OrdinalIgnoreCase)
                    ? AuthenticatorMigration.Parse(uri)
                    : [Parse(uri)];
        try
        {
            var merged = _accounts.ToList();
            foreach (var entry in added)
            {
                var index = merged.FindIndex(account => account.Issuer == entry.Issuer && account.Account == entry.Account);
                if (index < 0)
                {
                    merged.Add(entry);
                    continue;
                }

                var existing = merged[index];
                if (existing.Type != entry.Type || existing.Algorithm != entry.Algorithm || existing.Digits != entry.Digits ||
                    existing.Period != entry.Period || !CryptographicOperations.FixedTimeEquals(existing.Secret, entry.Secret))
                {
                    throw new FormatException("An existing account has conflicting OTP settings. Import cancelled.");
                }

                merged[index] = existing with { Counter = Math.Max(existing.Counter, entry.Counter) };
            }

            if (merged.Count > MAX_ACCOUNTS)
            {
                throw new FormatException("Account limit reached. Remove an account before importing.");
            }

            var updated = merged.ToArray();
            await SaveAsync(updated, token);
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _accounts = updated;
                added = added.Where(entry => !updated.Any(account => ReferenceEquals(account.Secret, entry.Secret))).ToArray();
            }
        }
        finally
        {
            Clear(added);
        }
    });

    public async Task<string?> GenerateHotpAsync(string id)
    {
        string? code = null;
        await RunAsync("generate HOTP", async token =>
        {
            EnsureLoaded();
            var account = _accounts.FirstOrDefault(account => account.Id == id)
                ?? throw new FormatException("Account not found. Reload the account list.");
            if (account.Type != "hotp" || account.Counter == long.MaxValue)
            {
                throw new FormatException("Account is not HOTP or its counter is exhausted.");
            }

            var generated = new Hotp(account.Secret, ParseAlgorithm(account.Algorithm), account.Digits).ComputeHOTP(account.Counter);
            var updated = _accounts.Select(entry => entry.Id == id ? entry with { Counter = account.Counter + 1 } : entry).ToArray();
            await SaveAsync(updated, token);
            lock (_sync)
            {
                // Once saved, publish the advanced state even if the request was cancelled meanwhile.
                ObjectDisposedException.ThrowIf(_disposed, this);
                _accounts = updated;
                code = generated;
            }
        });
        return code;
    }

    public Task RemoveAsync(string id) => RunAsync("remove", async token =>
    {
        EnsureLoaded();
        var removed = _accounts.FirstOrDefault(account => account.Id == id)
            ?? throw new FormatException("Account not found. Reload the account list.");
        var updated = _accounts.Where(account => account.Id != id).ToArray();
        await SaveAsync(updated, token);
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            _accounts = updated;
            CryptographicOperations.ZeroMemory(removed.Secret);
        }
    });

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lifetime.Cancel();
            Clear(_accounts);
            _accounts = [];
        }

        // The semaphore and cancellation source remain usable by in-flight waiters.
    }

    private async Task RunAsync(string operation, Func<CancellationToken, Task> action)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Interlocked.Increment(ref _pending);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var entered = false;
        try
        {
            await _operations.WaitAsync(timeout.Token);
            entered = true;
            await action(timeout.Token);
            lock (_sync)
            {
                _status = "Ready.";
            }
        }
        catch (Exception exception)
        {
            var message = exception switch {
                AuthenticatorValidationException => exception.Message,
                OperationCanceledException => "Operation cancelled or timed out. Unlock the desktop keyring and retry.",
                _ => "Operation failed. Check that the desktop Secret Service is running and the keyring is unlocked, then retry."
            };
            lock (_sync)
            {
                _status = message;
            }

            if (!_lifetime.IsCancellationRequested)
            {
                AppLogger.Warning("Authenticator", $"Could not {operation} accounts. {message}");
            }
        }
        finally
        {
            if (entered)
            {
                _operations.Release();
            }

            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task SaveAsync(AuthenticatorStoredAccount[] accounts, CancellationToken token)
    {
        // Secret Service requires immutable strings; these cannot be reliably erased in managed memory.
        string json;
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            json = JsonSerializer.Serialize(accounts, AuthenticatorJsonContext.Default.AuthenticatorStoredAccountArray);
        }

        await _store.WriteAsync(PROVIDER, json, token);
    }

    private void EnsureLoaded()
    {
        if (!_loaded)
        {
            throw new FormatException("Load accounts successfully before changing them.");
        }
    }

    private static AuthenticatorStoredAccount Parse(string uri)
    {
        if (string.IsNullOrEmpty(uri) || uri.Length > MAX_URI_LENGTH || uri.Any(char.IsControl))
        {
            throw new FormatException("Provide a valid otpauth://totp or otpauth://hotp URI.");
        }

        // Validate before Uri canonicalization can escape a malformed percent sequence.
        _ = Decode(uri);


        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "otpauth")
        {
            throw new FormatException("Provide an otpauth://totp or otpauth://hotp URI.");
        }

        if (parsed.Host is not ("totp" or "hotp") || parsed.UserInfo.Length != 0 || !parsed.IsDefaultPort || parsed.Fragment.Length != 0)
        {
            throw new FormatException("Only standard TOTP or HOTP otpauth URIs without credentials, port or fragment are supported.");
        }

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in parsed.Query.TrimStart('?').Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !query.TryAdd(Decode(parts[0]), Decode(parts[1])))
            {
                throw new FormatException("URI query parameters must have values and must not be repeated.");
            }
        }

        if (query.Keys.Any(key => key is not ("secret" or "issuer" or "algorithm" or "digits" or "period" or "counter")))
        {
            throw new FormatException("Unsupported URI parameter. Use only secret, issuer, algorithm, digits, period and counter.");
        }

        var label = Decode(parsed.AbsolutePath.TrimStart('/'));
        var split = label.Split(':', 2);
        var issuer = query.GetValueOrDefault("issuer", split.Length == 2 ? split[0] : "");
        var account = split[^1];
        if (string.IsNullOrWhiteSpace(account) || account.Length > 512 || issuer.Length > 512 ||
            label.Contains('/') || account.Contains(':') || issuer.Contains(':') ||
            (split.Length == 2 && (string.IsNullOrWhiteSpace(split[0]) || split[0] != issuer)))
        {
            throw new FormatException("Provide an account label and matching label/query issuer.");
        }

        var algorithm = query.GetValueOrDefault("algorithm", "SHA1");
        _ = ParseAlgorithm(algorithm);
        var digits = Number(query.GetValueOrDefault("digits", "6"));
        var period = Number(query.GetValueOrDefault("period", "30"));
        if (digits is not (6 or 8) || period is < 1 or > MAX_PERIOD)
        {
            throw new FormatException("Use 6 or 8 digits and a period between 1 and 86400 seconds.");
        }

        long counter = 0;
        if (parsed.Host == "hotp" && (!long.TryParse(query.GetValueOrDefault("counter"), NumberStyles.None, CultureInfo.InvariantCulture, out counter) || counter < 0))
        {
            throw new FormatException("HOTP requires a nonnegative decimal counter within the supported 64-bit range.");
        }

        var secret = DecodeSecret(query.GetValueOrDefault("secret", ""));
        return new(Guid.NewGuid().ToString("N"), issuer, account, secret, algorithm, digits, period, parsed.Host, counter);
    }

    private static string Decode(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2])))
            {
                throw new FormatException("URI contains invalid percent encoding.");
            }
        }

        var result = Uri.UnescapeDataString(value);
        if (result.Any(char.IsControl))
        {
            throw new FormatException("URI labels and parameters cannot contain control characters.");
        }

        return result;
    }

    private static int Number(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException("Digits and period must be positive decimal integers.");
        }

        return result;
    }

    private static OtpHashMode ParseAlgorithm(string algorithm) => algorithm switch {
        "SHA1" => OtpHashMode.Sha1,
        "SHA256" => OtpHashMode.Sha256,
        "SHA512" => OtpHashMode.Sha512,
        _ => throw new FormatException("Supported algorithms are SHA1, SHA256 and SHA512.")
    };

    private static byte[] DecodeSecret(string value)
    {
        if (value.Length is < 2 or > 1024 || value.Any(character => !(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '2' and <= '7')) ||
            value.Length % 8 is 1 or 3 or 6)
        {
            throw new FormatException("Secret must be valid unpadded Base32 (letters A-Z and digits 2-7).");
        }

        var bytes = new byte[value.Length * 5 / 8];
        var bits = 0;
        var buffer = 0;
        var index = 0;
        foreach (var character in value.ToUpperInvariant())
        {
            buffer = (buffer << 5) | (character <= 'Z' && character >= 'A' ? character - 'A' : character - '2' + 26);
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                bytes[index++] = (byte)(buffer >> bits);
            }
        }

        if ((buffer & ((1 << bits) - 1)) != 0)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new FormatException("Secret has invalid Base32 trailing bits.");
        }

        return bytes;
    }

    private static void ValidateStored(AuthenticatorStoredAccount[] accounts)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<(string, string)>();
        foreach (var account in accounts)
        {
            if (accounts.Length > MAX_ACCOUNTS || account is null || !Guid.TryParseExact(account.Id, "N", out _) ||
                !ids.Add(account.Id) || account.Issuer is null || string.IsNullOrWhiteSpace(account.Account) ||
                account.Issuer.Length > 512 || account.Account.Length > 512 ||
                account.Issuer.Any(char.IsControl) || account.Account.Any(char.IsControl) ||
                !labels.Add((account.Issuer, account.Account)) || account.Secret is not { Length: > 0 and <= 640 } ||
                account.Digits is not (6 or 8) || account.Period is < 1 or > MAX_PERIOD ||
                                account.Type is not ("totp" or "hotp") || account.Counter < 0)
            {
                throw new FormatException("Saved authenticator data is invalid. Restore a valid keyring backup.");
            }

            _ = ParseAlgorithm(account.Algorithm);
        }
    }

    private static void Clear(AuthenticatorStoredAccount[] accounts)
    {
        foreach (var account in accounts)
        {
            if (account?.Secret is { } secret)
            {
                CryptographicOperations.ZeroMemory(secret);
            }
        }
    }
}

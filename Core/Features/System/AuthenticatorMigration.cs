using System.Security.Cryptography;
using System.Text;

namespace HyprNetShell.Core.Features.System;

internal static class AuthenticatorMigration
{
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> _remaining = data;

        internal bool HasData => !_remaining.IsEmpty;

        internal ulong Varint()
        {
            ulong value = 0;
            for (var index = 0; index < 10; index++)
            {
                if (_remaining.IsEmpty)
                {
                    throw InvalidPayload();
                }

                var next = _remaining[0];
                _remaining = _remaining[1..];
                if (index == 9 && next > 1)
                {
                    throw InvalidPayload();
                }

                value |= (ulong)(next & 127) << (index * 7);
                if ((next & 128) == 0)
                {
                    return value;
                }
            }

            throw InvalidPayload();
        }

        internal (int Field, int Wire) Tag()
        {
            var tag = Varint();
            if (tag >> 3 is 0 or > 536870911)
            {
                throw InvalidPayload();
            }

            return ((int)(tag >> 3), (int)(tag & 7));
        }

        internal ReadOnlySpan<byte> Bytes()
        {
            var length = Varint();
            if (length > (ulong)_remaining.Length)
            {
                throw InvalidPayload();
            }

            var result = _remaining[..(int)length];
            _remaining = _remaining[(int)length..];
            return result;
        }

        internal void Skip(int wire)
        {
            switch (wire)
            {
                case 0:
                    _ = Varint();
                    break;
                case 1:
                    Advance(8);
                    break;
                case 2:
                    _ = Bytes();
                    break;
                case 5:
                    Advance(4);
                    break;
                default:
                    throw InvalidPayload();
            }
        }

        private void Advance(int count)
        {
            if (_remaining.Length < count)
            {
                throw InvalidPayload();
            }

            _remaining = _remaining[count..];
        }
    }

    private const int MAX_URI_LENGTH = 1024 * 1024;
    private const int MAX_PAYLOAD_LENGTH = 256 * 1024;
    private const int MAX_ACCOUNTS = 256;
    private const int MAX_SECRET_LENGTH = 640;
    private const int MAX_LABEL_LENGTH = 512;
    private static readonly UTF8Encoding UTF8 = new(false, true);

    internal static AuthenticatorStoredAccount[] Parse(string uri)
    {
        var accounts = new List<AuthenticatorStoredAccount>();
        byte[]? payload = null;
        var completed = false;
        try
        {
            var encoded = DataParameter(uri);
            // Decode directly into an owned buffer so even a partial failed decode can be erased.
            payload = new byte[Math.Min(MAX_PAYLOAD_LENGTH, encoded.Length / 4 * 3 + 3)];
            if (encoded.Any(char.IsWhiteSpace) || !Convert.TryFromBase64String(encoded, payload, out var length))
            {
                throw new AuthenticatorValidationException("Migration data is invalid or too large. Copy the complete Google Authenticator export URI again.");
            }

            var reader = new Reader(payload.AsSpan(0, length));
            var metadata = new Dictionary<int, ulong>();
            while (reader.HasData)
            {
                var (field, wire) = reader.Tag();
                if (field == 1)
                {
                    RequireWire(wire, 2);
                    if (accounts.Count >= MAX_ACCOUNTS)
                    {
                        throw new AuthenticatorValidationException("Migration page exceeds the 256-account limit. Export fewer accounts per page.");
                    }

                    accounts.Add(ParseAccount(reader.Bytes()));
                }
                else if (field is >= 2 and <= 5)
                {
                    RequireWire(wire, 0);
                    var value = reader.Varint();
                    // These metadata fields are int32; batch_id may be a signed int32.
                    var validInt32 = value <= int.MaxValue || (field == 5 && value >= 0xffffffff80000000UL);
                    if (!validInt32 || !metadata.TryAdd(field, value))
                    {
                        throw InvalidPayload();
                    }
                }
                else
                {
                    reader.Skip(wire);
                }
            }

            // Version is descriptive metadata in the published schema; validate the OTP fields
            // themselves rather than rejecting otherwise compatible exports by version number.
            if ((metadata.TryGetValue(3, out var size) && size == 0) ||
                (metadata.TryGetValue(4, out var index) && metadata.TryGetValue(3, out size) && index >= size))
            {
                throw new AuthenticatorValidationException("Migration page metadata is invalid. Export this page again.");
            }

            if (accounts.Count == 0)
            {
                throw new AuthenticatorValidationException("Migration page contains no accounts. Export a page containing accounts.");
            }

            var result = accounts.ToArray();
            completed = true;
            return result;
        }
        finally
        {
            if (payload is not null)
            {
                CryptographicOperations.ZeroMemory(payload);
            }

            if (!completed)
            {
                foreach (var account in accounts)
                {
                    CryptographicOperations.ZeroMemory(account.Secret);
                }
            }
        }
    }

    private static string DataParameter(string uri)
    {
        const string PREFIX = "otpauth-migration://offline?";
        if (string.IsNullOrEmpty(uri) || uri.Length > MAX_URI_LENGTH ||
            !uri.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase) || uri.Any(char.IsControl) || uri.Contains('#'))
        {
            throw new AuthenticatorValidationException("Provide a complete otpauth-migration://offline?data= export URI without a fragment.");
        }

        var query = uri[PREFIX.Length..];
        if (!query.StartsWith("data=", StringComparison.Ordinal) || query.Contains('&'))
        {
            throw new AuthenticatorValidationException("Migration URI must contain exactly one data parameter. Copy the original export URI.");
        }

        var encoded = query[5..];
        for (var index = 0; index < encoded.Length; index++)
        {
            if (encoded[index] == '%' && (index + 2 >= encoded.Length ||
                !Uri.IsHexDigit(encoded[index + 1]) || !Uri.IsHexDigit(encoded[index + 2])))
            {
                throw new AuthenticatorValidationException("Migration URI has invalid percent encoding. Copy the export URI again.");
            }
        }

        // '+' is a Base64 character, not form-url-encoded whitespace.
        return Uri.UnescapeDataString(encoded);
    }

    private static AuthenticatorStoredAccount ParseAccount(ReadOnlySpan<byte> data)
    {
        var reader = new Reader(data);
        ReadOnlySpan<byte> secret = default;
        var name = "";
        var issuer = "";
        ulong algorithm = 0;
        ulong digits = 0;
        ulong type = 0;
        ulong counter = 0;
        var seen = 0;
        while (reader.HasData)
        {
            var (field, wire) = reader.Tag();
            if (field is < 1 or > 7)
            {
                reader.Skip(wire);
                continue;
            }

            var mask = 1 << field;
            if ((seen & mask) != 0)
            {
                throw InvalidPayload();
            }

            seen |= mask;
            RequireWire(wire, field <= 3 ? 2 : 0);
            switch (field)
            {
                case 1:
                    secret = reader.Bytes();
                    break;
                case 2:
                    name = Label(reader.Bytes());
                    break;
                case 3:
                    issuer = Label(reader.Bytes());
                    break;
                case 4:
                    algorithm = reader.Varint();
                    break;
                case 5:
                    digits = reader.Varint();
                    break;
                case 6:
                    type = reader.Varint();
                    break;
                case 7:
                    counter = reader.Varint();
                    break;
            }
        }

        if (secret.IsEmpty || secret.Length > MAX_SECRET_LENGTH || string.IsNullOrWhiteSpace(name))
        {
            throw new AuthenticatorValidationException("Migration account requires a nonempty name and a secret of 1–640 bytes. Export the account again.");
        }

        if (algorithm is < 1 or > 3 || digits is < 1 or > 2 || type is < 1 or > 2 || counter > long.MaxValue)
        {
            throw new AuthenticatorValidationException("Migration account is unsupported. Use SHA1, SHA256 or SHA512, 6 or 8 digits, and HOTP or TOTP with a nonnegative 64-bit counter. No accounts were imported.");
        }

        var ownedSecret = secret.ToArray();
        try
        {
            return new AuthenticatorStoredAccount(Guid.NewGuid().ToString("N"), issuer, name, ownedSecret,
                algorithm == 1 ? "SHA1" : algorithm == 2 ? "SHA256" : "SHA512",
                digits == 1 ? 6 : 8, 30, type == 1 ? "hotp" : "totp", (long)counter);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(ownedSecret);
            throw;
        }
    }

    private static string Label(ReadOnlySpan<byte> data)
    {
        if (data.Length > MAX_LABEL_LENGTH * 4)
        {
            throw InvalidLabel();
        }

        string label;
        try
        {
            label = UTF8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            throw InvalidLabel();
        }

        if (label.Length > MAX_LABEL_LENGTH || label.Any(char.IsControl))
        {
            throw InvalidLabel();
        }

        return label;
    }

    private static void RequireWire(int actual, int expected)
    {
        if (actual != expected)
        {
            throw InvalidPayload();
        }
    }

    private static AuthenticatorValidationException InvalidPayload() =>
        new("Migration protobuf data is malformed or truncated. Copy the complete export page again; no accounts were imported.");

    private static AuthenticatorValidationException InvalidLabel() =>
        new("Migration account names and issuers must be valid UTF-8, at most 512 characters, without control characters. Correct the account and export again.");
}

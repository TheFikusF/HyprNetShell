using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.KdeConnect;

internal sealed class KdeConnectStateStore : IDisposable
{
    private const string LogCategory = "KdeConnect";
    private readonly Lock _gate = new();
    private readonly string _directory;
    private readonly string _pairedPath;
    private readonly Dictionary<string, PairedDevice> _paired = new(StringComparer.Ordinal);

    internal string DeviceId { get; }
    internal string DeviceName { get; }
    internal string DeviceType => "desktop";
    internal X509Certificate2 Certificate { get; }

    internal KdeConnectStateStore()
    {
        _directory = Path.Combine(
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
            "hyprnetshell",
            "kdeconnect");
        _pairedPath = Path.Combine(_directory, "paired-devices.json");

        Directory.CreateDirectory(_directory);
        SetDirectoryPermissions(_directory);

        var identityPath = Path.Combine(_directory, "identity.json");
        var certificatePath = Path.Combine(_directory, "identity.pfx");
        DeviceId = LoadDeviceId(identityPath) ?? Guid.NewGuid().ToString("N");
        DeviceName = SanitizeDeviceName(Environment.MachineName);
        Certificate = LoadOrCreateCertificate(certificatePath, DeviceId);
        WriteIdentity(identityPath, DeviceId);
        LoadPairedDevices();
    }

    internal IReadOnlyCollection<PairedDevice> PairedDevices
    {
        get
        {
            lock (_gate)
            {
                return _paired.Values.ToArray();
            }
        }
    }

    internal PairedDevice? GetPaired(string deviceId)
    {
        lock (_gate)
        {
            return _paired.GetValueOrDefault(deviceId);
        }
    }

    internal void SavePaired(PairedDevice device)
    {
        lock (_gate)
        {
            _paired[device.Id] = device;
            SavePairedDevices();
        }
    }

    internal void RemovePaired(string deviceId)
    {
        lock (_gate)
        {
            if (_paired.Remove(deviceId))
            {
                SavePairedDevices();
            }
        }
    }

    private static string? LoadDeviceId(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("deviceId", out var id) && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                return value is { Length: > 0 and <= 128 } ? value : null;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LogCategory, "Could not read the persistent KDE Connect device id", exception);
        }
        return null;
    }

    private static X509Certificate2 LoadOrCreateCertificate(string path, string deviceId)
    {
        if (File.Exists(path))
        {
            try
            {
                var loaded = X509CertificateLoader.LoadPkcs12FromFile(
                    path,
                    password: null,
                    X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable);
                if (loaded.HasPrivateKey &&
                    string.Equals(loaded.GetNameInfo(X509NameType.SimpleName, false), deviceId, StringComparison.Ordinal) &&
                    DateTime.UtcNow >= loaded.NotBefore.ToUniversalTime() &&
                    DateTime.UtcNow <= loaded.NotAfter.ToUniversalTime())
                {
                    SetFilePermissions(path);
                    return loaded;
                }
                loaded.Dispose();
                AppLogger.Warning(LogCategory, "The saved KDE Connect certificate is unusable; generating a replacement");
            }
            catch (Exception exception)
            {
                AppLogger.Warning(LogCategory, "Could not load the KDE Connect certificate; generating a replacement", exception);
            }
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={EscapeDistinguishedName(deviceId)}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
        AtomicWrite(path, generated.Export(X509ContentType.Pkcs12));
        return generated;
    }

    private void LoadPairedDevices()
    {
        if (!File.Exists(_pairedPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(_pairedPath));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Paired device state is not an array.");
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!TryReadPairedDevice(element, out var device))
                {
                    continue;
                }
                _paired[device.Id] = device;
            }
            SetFilePermissions(_pairedPath);
        }
        catch (Exception exception)
        {
            AppLogger.Warning(LogCategory, "Could not load paired KDE Connect devices; starting with none", exception);
        }
    }

    private static bool TryReadPairedDevice(JsonElement element, out PairedDevice device)
    {
        device = default!;
        if (!TryGetString(element, "id", out var id) ||
            !TryGetString(element, "name", out var name) ||
            !TryGetString(element, "deviceType", out var deviceType) ||
            !TryGetString(element, "certificate", out var certificateText))
        {
            return false;
        }

        try
        {
            var certificate = Convert.FromBase64String(certificateText);
            if (certificate.Length == 0 || id.Length is 0 or > 128)
            {
                return false;
            }
            device = new PairedDevice(id, name, deviceType, certificate);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private void SavePairedDevices()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var device in _paired.Values.OrderBy(static item => item.Id, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("id", device.Id);
                writer.WriteString("name", device.Name);
                writer.WriteString("deviceType", device.DeviceType);
                writer.WriteBase64String("certificate", device.CertificateDer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }
        AtomicWrite(_pairedPath, stream.ToArray());
    }

    private static void WriteIdentity(string path, string deviceId)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("deviceId", deviceId);
            writer.WriteEndObject();
        }
        AtomicWrite(path, stream.ToArray());
    }

    private static void AtomicWrite(string path, byte[] content)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content);
            SetFilePermissions(temporaryPath);
            File.Move(temporaryPath, path, true);
            SetFilePermissions(path);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static string SanitizeDeviceName(string value)
    {
        const string invalid = "\"',;:.!?()[]<>";
        var filtered = new string(value.Where(character => !invalid.Contains(character)).Take(32).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(filtered) ? "HyprNetShell" : filtered;
    }

    private static string EscapeDistinguishedName(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal);

    private static void SetDirectoryPermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetFilePermissions(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose() => Certificate.Dispose();
}

internal sealed record PairedDevice(string Id, string Name, string DeviceType, byte[] CertificateDer);

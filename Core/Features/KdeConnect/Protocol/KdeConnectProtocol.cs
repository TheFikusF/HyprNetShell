using System.Buffers;
using System.Text;
using System.Text.Json;

namespace HyprNetShell.Core.Features.KdeConnect.Protocol;

internal static class KdeConnectProtocol
{
    internal const int Version = 8;
    internal const int DiscoveryPort = 1716;
    internal const int LastPort = 1764;
    internal const int MaximumIdentityBytes = 8 * 1024;
    internal const int MaximumPacketBytes = 32 * 1024 * 1024;

    internal const string IdentityType = "kdeconnect.identity";
    internal const string PairType = "kdeconnect.pair";
    internal const string BatteryType = "kdeconnect.battery";
    internal const string BatteryRequestType = "kdeconnect.battery.request";
    internal const string ClipboardType = "kdeconnect.clipboard";
    internal const string ClipboardConnectType = "kdeconnect.clipboard.connect";

    internal static readonly string[] IncomingCapabilities =
    [
        BatteryType,
        ClipboardType,
        ClipboardConnectType,
    ];

    internal static readonly string[] OutgoingCapabilities =
    [
        BatteryRequestType,
        ClipboardType,
        ClipboardConnectType,
    ];

    internal static byte[] CreateDiscoveryIdentity(
        string deviceId,
        string deviceName,
        int tcpPort) => CreatePacket(IdentityType, writer =>
        {
            writer.WriteString("deviceId", deviceId);
            writer.WriteString("deviceName", deviceName);
            writer.WriteNumber("protocolVersion", Version);
            writer.WriteNumber("tcpPort", tcpPort);
        });

    internal static byte[] CreateConnectionIdentity(
        string deviceId,
        string deviceName,
        string targetDeviceId) => CreatePacket(IdentityType, writer =>
        {
            writer.WriteString("deviceId", deviceId);
            writer.WriteString("deviceName", deviceName);
            writer.WriteNumber("protocolVersion", Version);
            writer.WriteString("targetDeviceId", targetDeviceId);
            writer.WriteNumber("targetProtocolVersion", Version);
        });

    internal static byte[] CreateIdentity(
        string deviceId,
        string deviceName,
        string deviceType) => CreatePacket(IdentityType, writer =>
        {
            writer.WriteString("deviceId", deviceId);
            writer.WriteString("deviceName", deviceName);
            writer.WriteNumber("protocolVersion", Version);
            writer.WriteString("deviceType", deviceType);
            WriteStrings(writer, "incomingCapabilities", IncomingCapabilities);
            WriteStrings(writer, "outgoingCapabilities", OutgoingCapabilities);
        });

    internal static byte[] CreatePacket(string type, Action<Utf8JsonWriter>? writeBody = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            writer.WriteString("type", type);
            writer.WritePropertyName("body");
            writer.WriteStartObject();
            writeBody?.Invoke(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        var framed = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(framed);
        framed[^1] = (byte)'\n';
        return framed;
    }

    internal static bool TryParse(ReadOnlySpan<byte> utf8, out KdeConnectPacket? packet)
    {
        packet = null;
        try
        {
            var document = JsonDocument.Parse(utf8.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("id", out var idElement) ||
                !idElement.TryGetInt64(out var id) ||
                !root.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("body", out var body) ||
                body.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                return false;
            }

            var type = typeElement.GetString();
            if (string.IsNullOrWhiteSpace(type))
            {
                document.Dispose();
                return false;
            }

            packet = new KdeConnectPacket(document, id, type, body);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadIdentity(KdeConnectPacket packet, out KdeConnectIdentity identity)
    {
        identity = default;
        if (!string.Equals(packet.Type, IdentityType, StringComparison.Ordinal) ||
            !TryGetString(packet.Body, "deviceId", out var id) ||
            !TryGetString(packet.Body, "deviceName", out var name) ||
            !packet.Body.TryGetProperty("protocolVersion", out var versionElement) ||
            !TryGetInt32(versionElement, out var version))
        {
            return false;
        }

        var type = "unknown";
        if (packet.Body.TryGetProperty("deviceType", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            type = typeElement.GetString() ?? type;
        }
        var port = 0;
        if (packet.Body.TryGetProperty("tcpPort", out var portElement))
        {
            TryGetInt32(portElement, out port);
        }
        string? targetDeviceId = null;
        if (packet.Body.TryGetProperty("targetDeviceId", out var targetElement) && targetElement.ValueKind == JsonValueKind.String)
        {
            targetDeviceId = targetElement.GetString();
        }
        int? targetProtocolVersion = null;
        if (packet.Body.TryGetProperty("targetProtocolVersion", out var targetVersionElement) &&
            TryGetInt32(targetVersionElement, out var targetVersion))
        {
            targetProtocolVersion = targetVersion;
        }

        identity = new KdeConnectIdentity(id, name, type, version, port, targetDeviceId, targetProtocolVersion);
        return id.Length is >= 32 and <= 38 &&
            id.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') &&
            name.Length is > 0 and <= 256 && type.Length <= 64;
    }

    internal static bool TryGetString(JsonElement body, string name, out string value)
    {
        value = string.Empty;
        if (!body.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryGetInt32(JsonElement element, out int value)
    {
        if (element.ValueKind == JsonValueKind.Number)
        {
            return element.TryGetInt32(out value);
        }
        if (element.ValueKind == JsonValueKind.String)
        {
            return int.TryParse(
                element.GetString(),
                global::System.Globalization.NumberStyles.Integer,
                global::System.Globalization.CultureInfo.InvariantCulture,
                out value);
        }

        value = 0;
        return false;
    }

    private static void WriteStrings(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }
        writer.WriteEndArray();
    }
}

internal readonly record struct KdeConnectIdentity(
    string DeviceId,
    string DeviceName,
    string DeviceType,
    int ProtocolVersion,
    int TcpPort,
    string? TargetDeviceId = null,
    int? TargetProtocolVersion = null);

internal sealed class KdeConnectPacket : IDisposable
{
    private readonly JsonDocument _document;

    internal long Id
    {
        get;
    }
    internal string Type
    {
        get;
    }
    internal JsonElement Body
    {
        get;
    }

    internal KdeConnectPacket(JsonDocument document, long id, string type, JsonElement body)
    {
        _document = document;
        Id = id;
        Type = type;
        Body = body;
    }

    public void Dispose() => _document.Dispose();
}

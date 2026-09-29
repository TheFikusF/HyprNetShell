using System.Text.Json;
using HyprNetShell.Core.Features.KdeConnect.Protocol;
using HyprNetShell.Core.Features.KdeConnect.Transport;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.KdeConnect.Plugins;

internal sealed class ClipboardPlugin(ClipboardHistoryService clipboard) : IKdeConnectPlugin
{
    private readonly Lock _gate = new();
    private string? _lastRemoteText;
    private DateTimeOffset _lastRemoteAt;

    public bool CanHandle(string packetType) =>
        string.Equals(packetType, KdeConnectProtocol.ClipboardType, StringComparison.Ordinal) ||
        string.Equals(packetType, KdeConnectProtocol.ClipboardConnectType, StringComparison.Ordinal);

    public async Task HandleAsync(
        KdeConnectDevice device,
        KdeConnectChannel channel,
        KdeConnectPacket packet,
        CancellationToken cancellationToken)
    {
        if (!KdeConnectProtocol.TryGetString(packet.Body, "content", out var content))
        {
            return;
        }

        if (string.Equals(packet.Type, KdeConnectProtocol.ClipboardConnectType, StringComparison.Ordinal))
        {
            if (!packet.Body.TryGetProperty("timestamp", out var timestampElement) ||
                !timestampElement.TryGetInt64(out var timestamp) || timestamp <= 0)
            {
                return;
            }
            if (timestamp <= clipboard.LastChangeTimestamp)
            {
                return;
            }
        }

        lock (_gate)
        {
            _lastRemoteText = content;
            _lastRemoteAt = DateTimeOffset.UtcNow;
        }

        if (!await clipboard.CopyTextAsync(content, cancellationToken) && !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning("KdeConnect", $"Could not apply clipboard text received from '{device.Name}'");
        }
    }

    internal bool ShouldSuppressOutbound(string content)
    {
        lock (_gate)
        {
            return string.Equals(content, _lastRemoteText, StringComparison.Ordinal) &&
                DateTimeOffset.UtcNow - _lastRemoteAt < TimeSpan.FromSeconds(5);
        }
    }

    internal static byte[] CreateClipboardPacket(string content) => KdeConnectProtocol.CreatePacket(
        KdeConnectProtocol.ClipboardType,
        writer => writer.WriteString("content", content));

    internal static byte[] CreateConnectPacket(string content, long timestamp)
    {
        timestamp = timestamp > 0 ? timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return KdeConnectProtocol.CreatePacket(
            KdeConnectProtocol.ClipboardConnectType,
            writer =>
            {
                writer.WriteString("content", content);
                writer.WriteNumber("timestamp", timestamp);
            });
    }
}

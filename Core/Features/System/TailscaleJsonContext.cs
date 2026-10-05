using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Features.System;

internal sealed record TailscaleStatus
{
    public string? BackendState { get; init; }
        public Dictionary<string, TailscalePeerStatus?>? Peer { get; init; } = [];
}

internal sealed record TailscalePeerStatus
{
    public string HostName { get; init; } = "";
    public string? DNSName { get; init; } = "";
    public string? OS { get; init; } = "";
    public string[]? TailscaleIPs { get; init; } = [];
    public bool Online { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TailscaleStatus))]
internal sealed partial class TailscaleJsonContext : JsonSerializerContext;

namespace HyprNetShell.Core.Models;

public sealed record NetworkSnapshot(
    bool WifiAvailable,
    bool WifiEnabled,
    bool Connected,
    string Device,
    string Type,
    string Connection,
    IReadOnlyList<string> IpAddresses,
    int? WifiSignal,
    IReadOnlyList<NetworkTunnelSnapshot> Tunnels)
{
    public static NetworkSnapshot Empty { get; } = new(false, false, false, "", "", "", [], null, []);
}

public sealed record NetworkTunnelSnapshot(
    string Name,
    string Device,
    string Type,
    IReadOnlyList<string> IpAddresses,
    bool IsTailscale,
    IReadOnlyList<TailscalePeerSnapshot> Peers);

public sealed record TailscalePeerSnapshot(
    string Name,
    string OperatingSystem,
    IReadOnlyList<string> IpAddresses);

public sealed record WifiNetworkSnapshot(
    string Ssid,
    int? Signal,
    string Security,
    bool Active,
    string? SavedConnectionName);

internal readonly record struct WifiOperationResult(bool Success, string? Error)
{
    public static WifiOperationResult Succeeded { get; } = new(true, null);
    public static WifiOperationResult Failed(string? error) => new(false, error);
}

internal readonly record struct WifiPasswordResult(bool Success, string? Password, string? Error)
{
    public static WifiPasswordResult Succeeded(string password) => new(true, password, null);
    public static WifiPasswordResult Failed(string? error) => new(false, null, error);
}

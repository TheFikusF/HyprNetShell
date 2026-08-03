namespace HyprNetShell.Core.Models;

public sealed record NetworkSnapshot(
    bool WifiAvailable,
    bool WifiEnabled,
    bool Connected,
    string Device,
    string Type,
    string Connection,
    IReadOnlyList<string> IpAddresses,
    int? WifiSignal)
{
    public static NetworkSnapshot Empty { get; } = new(false, false, false, "", "", "", [], null);
}

public sealed record WifiNetworkSnapshot(
    string Ssid,
    int? Signal,
    string Security,
    bool Active);

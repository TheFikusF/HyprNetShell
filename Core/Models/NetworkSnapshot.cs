namespace HyprNetShell.Core.Models;

public sealed record NetworkSnapshot(
    bool Connected,
    string Device,
    string Type,
    string Connection,
    IReadOnlyList<string> IpAddresses,
    int? WifiSignal)
{
    public static NetworkSnapshot Empty { get; } = new(false, "", "", "", [], null);
}

public sealed record WifiNetworkSnapshot(
    string Ssid,
    int? Signal,
    string Security,
    bool Active);

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

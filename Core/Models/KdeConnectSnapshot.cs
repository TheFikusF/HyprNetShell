namespace HyprNetShell.Core.Models;

public enum KdeConnectPairingState
{
    Unpaired,
    IncomingRequest,
    OutgoingRequest,
    Paired,
}

public sealed record KdeConnectDeviceSnapshot(
    string Id,
    string Name,
    string DeviceType,
    bool IsReachable,
    bool IsPaired,
    int? BatteryLevel,
    bool? IsCharging,
    KdeConnectPairingState PairingState,
    string? VerificationCode);

public sealed record KdeConnectSnapshot(IReadOnlyList<KdeConnectDeviceSnapshot> Devices)
{
    public static KdeConnectSnapshot Empty { get; } = new([]);
}

internal readonly record struct KdeConnectOperationResult(bool Success, string? Error)
{
    internal static KdeConnectOperationResult Succeeded { get; } = new(true, null);
    internal static KdeConnectOperationResult Failed(string error) => new(false, error);
}

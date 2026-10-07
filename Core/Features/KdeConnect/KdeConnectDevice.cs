using HyprNetShell.Core.Features.KdeConnect.Transport;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.KdeConnect;

internal sealed class KdeConnectDevice
{
    internal string Id
    {
        get;
    }
    internal string Name
    {
        get; set;
    }
    internal string DeviceType
    {
        get; set;
    }
    internal string? Address
    {
        get; set;
    }
    internal int TcpPort
    {
        get; set;
    }
    internal DateTimeOffset LastSeen
    {
        get; set;
    }
    internal bool IsPaired
    {
        get; set;
    }
    internal KdeConnectPairingState PairingState
    {
        get; set;
    }
    internal int? BatteryLevel
    {
        get; set;
    }
    internal bool? IsCharging
    {
        get; set;
    }
    internal string? VerificationCode
    {
        get; set;
    }
    internal long? PairingTimestamp
    {
        get; set;
    }
    internal KdeConnectChannel? Channel
    {
        get; set;
    }
    internal DateTimeOffset ChannelConnectedAt
    {
        get; set;
    }
    internal DateTimeOffset NextReconnectAt
    {
        get; set;
    }
    internal int ReconnectAttempts
    {
        get; set;
    }
    internal CancellationTokenSource? PairingTimeout
    {
        get; set;
    }
    internal bool Connecting
    {
        get; set;
    }
    internal bool ConnectionFailed
    {
        get; set;
    }
    internal string? LastConnectionFailure
    {
        get; set;
    }
    internal DateTimeOffset NextConnectionWarningAt
    {
        get; set;
    }

    internal bool IsReachable => Channel is not null ||
        (!ConnectionFailed && DateTimeOffset.UtcNow - LastSeen < TimeSpan.FromSeconds(45));

    internal KdeConnectDevice(string id, string name, string deviceType)
    {
        Id = id;
        Name = name;
        DeviceType = deviceType;
        PairingState = KdeConnectPairingState.Unpaired;
    }

    internal KdeConnectDeviceSnapshot ToSnapshot() => new(
        Id,
        Name,
        DeviceType,
        IsReachable,
        IsPaired,
        BatteryLevel,
        IsCharging,
        PairingState,
        VerificationCode);
}

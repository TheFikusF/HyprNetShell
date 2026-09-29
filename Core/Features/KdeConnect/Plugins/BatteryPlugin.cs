using System.Text.Json;
using HyprNetShell.Core.Features.KdeConnect.Protocol;
using HyprNetShell.Core.Features.KdeConnect.Transport;

namespace HyprNetShell.Core.Features.KdeConnect.Plugins;

internal sealed class BatteryPlugin(Action<KdeConnectDevice> changed) : IKdeConnectPlugin
{
    public bool CanHandle(string packetType) =>
        string.Equals(packetType, KdeConnectProtocol.BatteryType, StringComparison.Ordinal);

    public Task HandleAsync(
        KdeConnectDevice device,
        KdeConnectChannel channel,
        KdeConnectPacket packet,
        CancellationToken cancellationToken)
    {
        if (!packet.Body.TryGetProperty("currentCharge", out var chargeElement) ||
            !chargeElement.TryGetInt32(out var charge) ||
            charge is < 0 or > 100)
        {
            return Task.CompletedTask;
        }

        bool? charging = null;
        if (packet.Body.TryGetProperty("isCharging", out var chargingElement) &&
            chargingElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            charging = chargingElement.GetBoolean();
        }

        device.BatteryLevel = charge;
        device.IsCharging = charging;
        changed(device);
        return Task.CompletedTask;
    }
}

using HyprNetShell.Core.Features.KdeConnect.Protocol;
using HyprNetShell.Core.Features.KdeConnect.Transport;

namespace HyprNetShell.Core.Features.KdeConnect.Plugins;

internal interface IKdeConnectPlugin
{
    bool CanHandle(string packetType);

    Task HandleAsync(
        KdeConnectDevice device,
        KdeConnectChannel channel,
        KdeConnectPacket packet,
        CancellationToken cancellationToken);
}

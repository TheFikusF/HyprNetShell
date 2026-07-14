using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HyprNetShell.Core.Features.System;

internal sealed class NetworkModuleService : IBarDataService
{
    public async ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        var output = await CommandRunner.TryReadAsync(
            "nmcli",
            "-t -f DEVICE,TYPE,STATE,CONNECTION device",
            TimeSpan.FromMilliseconds(800),
            cancellationToken);

        var snapshot = NetworkSnapshot.Empty;

        if (!string.IsNullOrWhiteSpace(output))
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(':');
                if (parts.Length < 4)
                {
                    continue;
                }

                var device = parts[0];
                var type = parts[1];
                var stateName = parts[2];
                var connection = parts[3];

                if (stateName == "connected" && !string.IsNullOrWhiteSpace(connection))
                {
                    var wifiSignal = type.Equals("wifi", StringComparison.OrdinalIgnoreCase)
                        ? await ReadWifiSignalAsync(device, cancellationToken)
                        : null;
                    snapshot = new NetworkSnapshot(
                        true,
                        device,
                        type,
                        connection,
                        ReadIpAddresses(device),
                        wifiSignal);
                    break;
                }
            }
        }

        state.Network = snapshot;
    }

    private static async Task<int?> ReadWifiSignalAsync(string device, CancellationToken cancellationToken)
    {
        var output = await CommandRunner.TryReadAsync(
            "nmcli",
            $"-t -f ACTIVE,SIGNAL device wifi list ifname {device}",
            TimeSpan.FromMilliseconds(800),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.LastIndexOf(':');
            if (separator <= 0 ||
                !line[..separator].Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(line[(separator + 1)..], out var signal))
            {
                continue;
            }

            return Math.Clamp(signal, 0, 100);
        }

        return null;
    }

    private static IReadOnlyList<string> ReadIpAddresses(string device)
    {
        try
        {
            var networkInterface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => string.Equals(x.Name, device, StringComparison.Ordinal));
            if (networkInterface is null)
            {
                return [];
            }

            return networkInterface.GetIPProperties()
                .UnicastAddresses
                .Where(x => x.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                .Where(x => !x.Address.IsIPv6LinkLocal)
                .Select(x => x.Address.ToString())
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}

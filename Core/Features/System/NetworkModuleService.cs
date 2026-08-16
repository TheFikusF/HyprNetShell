using System.Diagnostics;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HyprNetShell.Core.Features.System;

internal sealed class NetworkModuleService : IBarDataService
{
    public NetworkSnapshot Snapshot { get; private set; } = NetworkSnapshot.Empty;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var radioTask = CommandRunner.TryReadAsync(
            "nmcli",
            "radio wifi",
            TimeSpan.FromMilliseconds(800),
            cancellationToken);
        var devicesTask = CommandRunner.TryReadAsync(
            "nmcli",
            "-t -f DEVICE,TYPE,STATE,CONNECTION device",
            TimeSpan.FromMilliseconds(800),
            cancellationToken);
        await Task.WhenAll(radioTask, devicesTask);

        var radioOutput = await radioTask;
        var output = await devicesTask;
        var wifiAvailable = radioOutput is not null;
        var wifiEnabled = radioOutput?.Trim().Equals("enabled", StringComparison.OrdinalIgnoreCase) == true;

        var snapshot = new NetworkSnapshot(wifiAvailable, wifiEnabled, false, "", "", "", [], null);

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

                if (stateName != "connected" || string.IsNullOrWhiteSpace(connection))
                {
                    continue;
                }

                var wifiSignal = type.Equals("wifi", StringComparison.OrdinalIgnoreCase)
                    ? await ReadWifiSignalAsync(device, cancellationToken)
                    : null;
                
                snapshot = new NetworkSnapshot(
                    wifiAvailable,
                    wifiEnabled,
                    true,
                    device,
                    type,
                    connection,
                    ReadIpAddresses(device),
                    wifiSignal);
                
                break;
            }
        }

        Snapshot = snapshot;
    }

    internal Task SetWifiEnabledAsync(bool enabled) =>
        CommandRunner.TryRunAsync(
            "nmcli",
            ["radio", "wifi", enabled ? "on" : "off"],
            TimeSpan.FromSeconds(3),
            CancellationToken.None);

    internal async Task<IReadOnlyList<WifiNetworkSnapshot>> ScanWifiNetworksAsync()
    {
        var output = await CommandRunner.TryReadAsync(
            "nmcli",
            "-t -f ACTIVE,SSID,SIGNAL,SECURITY d wifi list",
            TimeSpan.FromSeconds(3),
            CancellationToken.None);
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var networks = new List<WifiNetworkSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = SplitNmcliFields(line);
            if (parts.Length < 4 || !seen.Add(parts[1]))
            {
                continue;
            }

            networks.Add(new WifiNetworkSnapshot(
                parts[1],
                int.TryParse(parts[2], out var signal) ? signal : null,
                parts[3],
                parts[0].Equals("yes", StringComparison.OrdinalIgnoreCase)));
        }

        return networks
            .OrderByDescending(x => x.Active)
            .ThenByDescending(x => x.Signal.GetValueOrDefault())
            .ToArray();
    }

    internal void ConnectWifi(string ssid)
    {
        Task.Run(() =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "nmcli",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "d", "wifi", "connect", ssid },
                });
            }
            catch
            {
                // Ignore transient command failures; the next state refresh will show the result.
            }
        });
    }

    private static string[] SplitNmcliFields(string line)
    {
        var fields = new List<string>();
        var current = new global::System.Text.StringBuilder();
        var escaped = false;

        foreach (var c in line)
        {
            if (escaped)
            {
                current.Append(c);
                escaped = false;
            }
            else if (c == '\\')
            {
                escaped = true;
            }
            else if (c == ':')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
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

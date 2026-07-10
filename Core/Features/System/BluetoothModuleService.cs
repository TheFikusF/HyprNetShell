using System.Text.RegularExpressions;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed partial class BluetoothModuleService : IBarDataService
{
    public async ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        var devicesOutput = await CommandRunner.TryReadAsync(
            "bluetoothctl",
            "devices Paired",
            TimeSpan.FromMilliseconds(700),
            cancellationToken);

        if (devicesOutput is null)
        {
            state.Bluetooth = BluetoothSnapshot.Empty;
            return;
        }

        var devices = await Task.WhenAll(ParsePairedDevices(devicesOutput).Select(async device =>
        {
            var info = await CommandRunner.TryReadAsync(
                "bluetoothctl",
                $"info {device.Address}",
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
            return ParseDeviceInfo(device.Address, device.Name, info);
        }));

        state.Bluetooth = new BluetoothSnapshot(
            true,
            devices.OrderByDescending(device => device.Connected).ThenBy(device => device.Name).ToArray());
    }

    internal static Task SetConnectedAsync(string address, bool connected) =>
        CommandRunner.TryRunAsync(
            "bluetoothctl",
            [connected ? "connect" : "disconnect", address],
            TimeSpan.FromSeconds(8),
            CancellationToken.None);

    internal static IReadOnlyList<(string Address, string Name)> ParsePairedDevices(string output)
    {
        var devices = new List<(string, string)>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = DeviceLine().Match(line);
            if (match.Success)
            {
                devices.Add((match.Groups["address"].Value, match.Groups["name"].Value.Trim()));
            }
        }

        return devices;
    }

    internal static BluetoothDeviceSnapshot ParseDeviceInfo(string address, string name, string? output)
    {
        var connected = false;
        int? battery = null;
        if (!string.IsNullOrWhiteSpace(output))
        {
            connected = ConnectedLine().IsMatch(output);
            var batteryMatch = BatteryLine().Match(output);
            if (batteryMatch.Success && int.TryParse(batteryMatch.Groups["percentage"].Value, out var percentage))
            {
                battery = Math.Clamp(percentage, 0, 100);
            }
        }

        return new BluetoothDeviceSnapshot(address, name, connected, battery);
    }

    [GeneratedRegex(@"^Device\s+(?<address>[0-9A-Fa-f:]{17})\s+(?<name>.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();

    [GeneratedRegex(@"^\s*Connected:\s*yes\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectedLine();

    [GeneratedRegex(@"^\s*Battery Percentage:\s*(?:0x[0-9A-Fa-f]+\s+)?\((?<percentage>\d+)\)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BatteryLine();
}

using System.Diagnostics;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class NetworkModule(
    Func<NetworkSnapshot> snapshot,
    Theme theme) : IDrawableModule
{
    private static readonly TimeSpan WifiScanInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private IReadOnlyList<WifiNetworkSnapshot> _wifiNetworks = [];
    private DateTime _lastWifiScan = DateTime.MinValue;
    private Task? _wifiScanTask;

    private readonly ModulesCommon.NodeWithPopup _node = new("network_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var network = snapshot();
        if (_node.IsHovered)
        {
            RefreshWifiNetworks();
        }

        return _node.Draw([BuildStateModule(network)], () => BuildPopup(network));
    }

    private Node WifiIcon(int strength, int size)
    {
        return new BoxNode(size, size)
        {
            new BoxNode
            {
                IgnoreLayout = true,
                Children = [new ImageNode(Icons.WifiStrength[^1], 18, 18, theme.Text with { A = 0.3f })]
            },
            new BoxNode
            {
                IgnoreLayout = true, Children = [new ImageNode(Icons.WifiStrength[strength], 18, 18, theme.Text)]
            }
        };
    }

    private Node BuildStateModule(NetworkSnapshot network)
    {
        var icon = !network.Connected
            ? new ImageNode(Icons.WifiOff, 18, 18, theme.Text)
            : network.Type.Equals("wifi", StringComparison.OrdinalIgnoreCase)
                ? WifiIcon(WifiStrengthIndex(network.WifiSignal), 18)
                : network.Type.Equals("ethernet", StringComparison.OrdinalIgnoreCase)
                    ? new ImageNode(Icons.Ethernet, 18, 18, theme.Text)
                    : new ImageNode(Icons.Globe, 18, 18, theme.Text);

        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme,
                ModulesCommon.ToBackground(theme, Color.Lerp(Color.Green, Color.Blue, 0.3f)), left: false),
            Children = [icon],
        };
    }

    private BoxNode BuildPopup(NetworkSnapshot network) => new BoxNode(360)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(theme),
        Children =
        [
            new TextNode("Available Wi-Fi", 14.0f, theme.Text),
            ..BuildWifiRows(),
            ModulesCommon.BuildDivider(theme.Border),
            new TextNode("Details", 14.0f, theme.Text),
            BuildIpRow(network.Device),
            ..BuildIpRows(network),
        ]
    };

    private IEnumerable<Node> BuildWifiRows()
    {
        if (_wifiScanTask is { IsCompleted: false } && _wifiNetworks.Count == 0)
        {
            yield return BuildPlainRow("Scanning...");
            yield break;
        }

        if (_wifiNetworks.Count == 0)
        {
            yield return BuildPlainRow("No networks found");
            yield break;
        }

        foreach (var wifi in _wifiNetworks.Take(8))
        {
            yield return BuildWifiRow(wifi);
        }
    }

    private IEnumerable<Node> BuildIpRows(NetworkSnapshot network)
    {
        if (network.IpAddresses.Count == 0)
        {
            yield return BuildPlainRow("No IP address");
        }

        foreach (var ipAddress in network.IpAddresses)
        {
            yield return BuildIpRow(ipAddress);
        }
    }

    private BoxNode BuildWifiRow(WifiNetworkSnapshot wifi)
    {
        var state = _rowStates.GetState($"wifi:{wifi.Ssid}", theme.Panel).UpdateColor(wifi.Active ? theme.Active : theme.Panel);
        var ssid = string.IsNullOrWhiteSpace(wifi.Ssid) ? "<hidden>" : wifi.Ssid;
        var security = string.IsNullOrWhiteSpace(wifi.Security) ? "open" : wifi.Security;
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = string.IsNullOrWhiteSpace(wifi.Ssid) ? null : () => ConnectWifi(wifi.Ssid),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Spacing = 12,
                BorderRadius = 8,
                BorderWidth = wifi.Active ? theme.BorderWidth : 0
            },
            Children =
            [
                wifi.Active ? new ImageNode(Icons.Check, 14, 14, theme.Text) : new BoxNode(16, 16),
                WifiIcon(WifiStrengthIndex(wifi.Signal), 18),
                new TextNode(Trim(ssid, 22), 14.0f, theme.Text),
                new TextNode(security, 14.0f, theme.Text),
            ],
        };
    }

    private BoxNode BuildIpRow(string ipAddress)
    {
        var state = _rowStates.GetState($"ip:{ipAddress}", theme.Panel).UpdateColor(theme.Panel);
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => Utils.CopyToClipboard(ipAddress),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Spacing = 8,
                BorderRadius = 8
            },
            Children =
            [
                new ImageNode(Icons.Copy, 14, 14, theme.Text),
                new TextNode(ipAddress, 14.0f, theme.Text),
            ],
        };
    }

    private Node BuildPlainRow(string text) =>
        new BoxNode
        {
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8 },
            Children = [new TextNode(text, 14.0f, theme.Muted)],
        };

    private void RefreshWifiNetworks()
    {
        if (_wifiScanTask is { IsCompleted: false } || DateTime.UtcNow - _lastWifiScan < WifiScanInterval)
        {
            return;
        }

        _lastWifiScan = DateTime.UtcNow;
        _wifiScanTask = Task.Run(async () =>
        {
            var output = await CommandRunner.TryReadAsync(
                "nmcli",
                "-t -f ACTIVE,SSID,SIGNAL,SECURITY d wifi list",
                TimeSpan.FromSeconds(3),
                CancellationToken.None);
            _wifiNetworks = ParseWifiNetworks(output);
        });
    }

    private static IReadOnlyList<WifiNetworkSnapshot> ParseWifiNetworks(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var networks = new List<WifiNetworkSnapshot>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = SplitNmcliFields(line);
            if (parts.Length < 4)
            {
                continue;
            }

            var ssid = parts[1];
            if (!seen.Add(ssid))
            {
                continue;
            }

            networks.Add(new WifiNetworkSnapshot(
                ssid,
                int.TryParse(parts[2], out var signal) ? signal : null,
                parts[3],
                parts[0].Equals("yes", StringComparison.OrdinalIgnoreCase)));
        }

        return networks
            .OrderByDescending(x => x.Active)
            .ThenByDescending(x => x.Signal.GetValueOrDefault())
            .ToArray();
    }

    private static string[] SplitNmcliFields(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
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

    private static int WifiStrengthIndex(int? signal) => signal switch
    {
        null or <= 25 => 0,
        <= 50 => 1,
        <= 75 => 2,
        _ => 3,
    };

    private static void ConnectWifi(string ssid)
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

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
}

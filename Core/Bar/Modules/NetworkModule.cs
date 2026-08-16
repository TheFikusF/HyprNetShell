using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class NetworkModule(
    NetworkModuleService service,
    Theme theme) : IDrawableModule
{
    private static readonly TimeSpan WifiScanInterval = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private IReadOnlyList<WifiNetworkSnapshot> _wifiNetworks = [];
    private DateTime _lastWifiScan = DateTime.MinValue;
    private Task? _wifiScanTask;
    private readonly RefFloat _wifiSwitchAnimation = new();
    private bool? _wifiEnabledOverride;

    private readonly ModulesCommon.NodeWithPopup _node = new("network_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    public Node Draw()
    {
        var network = service.Snapshot;
        if (_node.IsHovered)
        {
            RefreshWifiNetworks(EffectiveWifiEnabled(network));
        }

        return _node.Draw([BuildStateModule(network)], () => BuildPopup(network));
    }

    private BoxNode WifiIcon(int strength, int size)
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

    private BoxNode BuildStateModule(NetworkSnapshot network)
    {
        Node icon = !network.Connected
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
            BuildWifiPowerRow(network),
            ..BuildWifiRows(EffectiveWifiEnabled(network)),
            ModulesCommon.BuildDivider(theme.Border),
            ModulesCommon.BuildTextWithIcon(theme, Icons.Info, "Details"),
            BuildIpRow(network.Device),
            ..BuildIpRows(network),
        ]
    };

    private BoxNode BuildWifiPowerRow(NetworkSnapshot network)
    {
        var enabled = EffectiveWifiEnabled(network);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = network.WifiAvailable ? () => SetWifiEnabled(network, !enabled) : null,
            Style = new Style()
            {
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new BoxNode(48),
                ModulesCommon.BuildTextWithIcon(theme, Icons.WifiStrength[^1], "Wi-Fi"),
                new SwitchNode(enabled, _wifiSwitchAnimation)
                {
                    OffTrackColor = theme.Muted,
                    OnTrackColor = theme.Active,
                    KnobColor = theme.Text,
                },
            ],
        };
    }

    private IEnumerable<Node> BuildWifiRows(bool enabled)
    {
        if (!enabled)
        {
            yield return BuildPlainRow("Wi-Fi is off");
            yield break;
        }

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
        var state = _rowStates.GetState($"wifi:{wifi.Ssid}", theme.Panel).UpdateColor(theme.Panel);
        var ssid = string.IsNullOrWhiteSpace(wifi.Ssid) ? "<hidden>" : wifi.Ssid;
        var security = string.IsNullOrWhiteSpace(wifi.Security) ? "open" : wifi.Security;
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = wifi.Active || string.IsNullOrWhiteSpace(wifi.Ssid)
                ? null
                : () => service.ConnectWifi(wifi.Ssid),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Spacing = 12,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new RadioButtonNode(wifi.Active)
                {
                    SelectedColor = Color.Orange,
                    UnselectedColor = theme.Muted,
                    BackgroundColor = theme.Panel,
                },
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

    private void RefreshWifiNetworks(bool enabled)
    {
        if (!enabled)
        {
            _wifiNetworks = [];
            return;
        }

        if (_wifiScanTask is { IsCompleted: false } || DateTime.UtcNow - _lastWifiScan < WifiScanInterval)
        {
            return;
        }

        _lastWifiScan = DateTime.UtcNow;
        _wifiScanTask = Task.Run(async () =>
        {
            _wifiNetworks = await service.ScanWifiNetworksAsync();
        });
    }

    private bool EffectiveWifiEnabled(NetworkSnapshot network)
    {
        if (_wifiEnabledOverride is not { } enabled)
        {
            return network.WifiEnabled;
        }

        if (enabled != network.WifiEnabled)
        {
            return enabled;
        }

        _wifiEnabledOverride = null;
        return network.WifiEnabled;
    }

    private void SetWifiEnabled(NetworkSnapshot network, bool enabled)
    {
        if (!network.WifiAvailable)
        {
            return;
        }

        _wifiEnabledOverride = enabled;
        _wifiNetworks = [];
        _lastWifiScan = DateTime.MinValue;
        _ = service.SetWifiEnabledAsync(enabled);
    }

    private static int WifiStrengthIndex(int? signal) => signal switch
    {
        null or <= 25 => 0,
        <= 50 => 1,
        <= 75 => 2,
        _ => 3,
    };

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
}

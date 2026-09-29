using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;

using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class NetworkModule(
    NetworkModuleService service,
    DialogService dialogs,
    TabsService tabs,
    ClipboardHistoryService clipboard,
    Theme theme,
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private const string WifiPopupTab = "wifi";
    private const string VpnPopupTab = "vpn";
    private const string DetailsPopupTab = "details";
    private static readonly TimeSpan WifiScanInterval = TimeSpan.FromSeconds(5);

    private readonly ModulesCommon.BoxState _settingsState = new();
    private readonly SegmentedSwitch _popupTabSwitch = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private IReadOnlyList<WifiNetworkSnapshot> _wifiNetworks = [];
    private DateTime _lastWifiScan = DateTime.MinValue;
    private Task? _wifiScanTask;
    private readonly Ref<float> _wifiSwitchAnimation = new();
    private bool? _wifiEnabledOverride;
    private string _selectedPopupTab = WifiPopupTab;

    private readonly NodeWithPopup _node = new(popupCoordinator, "network_module")
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
                Children = [new ImageNode(Icons.WifiStrength[^1], 18, 18, theme.Text.Color with { A = 0.3f })]
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

        var background = ModulesCommon.ToBackground(theme, Color.Lerp(Color.Green, Color.Blue, 0.3f));
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, background, left: false) with
            {
                ShadowColor = null
            },
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
            _popupTabSwitch.Build(
                theme,
                [
                    new SegmentedSwitch.Item(WifiPopupTab, new TextNode("Wi-Fi", theme.Text, theme.Text)),
                    new SegmentedSwitch.Item(VpnPopupTab, new TextNode("VPN", theme.Text, theme.Text)),
                    new SegmentedSwitch.Item(DetailsPopupTab, new TextNode("Details", theme.Text, theme.Text)),
                ],
                _selectedPopupTab,
                selected => _selectedPopupTab = selected),
            ..BuildSelectedPopupTab(network),
        ]
    };

    private IReadOnlyList<Node> BuildSelectedPopupTab(NetworkSnapshot network) => _selectedPopupTab switch
    {
        VpnPopupTab => [..BuildTunnelRows(network.Tunnels)],
        DetailsPopupTab =>
        [
            BuildDetailRow("Status", network.Connected ? "Connected" : "Disconnected"),
            BuildDetailRow("Device", network.Device),
            BuildDetailRow("Connection", network.Connection),
            BuildDetailRow("Type", network.Type),
            ..BuildIpRows(network),
        ],
        _ => [BuildWifiPowerRow(network), ..BuildWifiRows(EffectiveWifiEnabled(network))],
    };

    private void OpenWifiSettings()
    {
        _node.ClosePopup();
        dialogs.Open<CompositeWindow>([tabs.Get<WifiTab>()]);
    }

    private BoxNode BuildWifiPowerRow(NetworkSnapshot network)
    {
        var enabled = EffectiveWifiEnabled(network);
        _settingsState.UpdateColor(theme.Panel);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style()
            {
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new BoxNode(48 + 8 + 20 + 8),
                ModulesCommon.BuildTextWithIcon(theme, Icons.WifiStrength[^1], "Wi-Fi"),
                new BoxNode(Style.Spacer, ItemsAlignment.Center, ItemsAlignment.Center)
                {
                    new BoxNode()
                    {
                        HorizontalAlignment = ItemsAlignment.Spread,
                        VerticalAlignment = ItemsAlignment.Center,
                        IsHovered = _settingsState.Hovered,
                        OnClick = network.WifiAvailable ? OpenWifiSettings : null,
                        Style = ModulesCommon.ModuleStyle(theme, _settingsState.Background) with
                        {
                            Padding = 4,
                            BorderRadius = 8,
                            BorderWidth = 0,
                        },
                        Children = [new ImageNode(Icons.Settings, 20, 20, theme.Text)]
                    },
                    new BoxNode()
                    {
                        OnClick = network.WifiAvailable ? () => SetWifiEnabled(network, !enabled) : null,
                        Children =
                        [
                            new SwitchNode(enabled, _wifiSwitchAnimation)
                            {
                                OffTrackColor = theme.Text.MutedColor,
                                OnTrackColor = theme.Active,
                                KnobColor = theme.Text,
                            }
                        ]
                    }
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

    private IEnumerable<Node> BuildTunnelRows(IReadOnlyList<NetworkTunnelSnapshot> tunnels)
    {
        if (tunnels.Count == 0)
        {
            yield return BuildPlainRow("No active VPN connections");
            yield break;
        }

        foreach (var tunnel in tunnels.OrderByDescending(tunnel => tunnel.IsTailscale))
        {
            yield return BuildTunnelRow(tunnel);
        }
    }

    private BoxNode BuildTunnelRow(NetworkTunnelSnapshot tunnel) => new()
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
        {
            BorderRadius = 8,
            BorderWidth = 0,
            Spacing = 6,
        },
        Children =
        [
            new BoxNode
            {
                HorizontalAlignment = ItemsAlignment.Spread,
                VerticalAlignment = ItemsAlignment.Center,
                Children =
                [
                    ModulesCommon.BuildTextWithIcon(
                        theme,
                        tunnel.IsTailscale ? Icons.Globe : Icons.Lock,
                        tunnel.Name,
                        maxTextWidth: 220),
                    new TextNode("Active", theme.Text, theme.Text.MutedColor),
                ],
            },
            BuildDetailRow("Device", tunnel.Device),
            BuildDetailRow("Type", tunnel.Type),
            ..tunnel.IpAddresses.Select(BuildIpRow),
            ..BuildTailscalePeerRows(tunnel),
        ],
    };

    private IEnumerable<Node> BuildTailscalePeerRows(NetworkTunnelSnapshot tunnel)
    {
        if (!tunnel.IsTailscale)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(theme.Border, height: 12);
        yield return BuildDetailRow("Devices online", tunnel.Peers.Count.ToString());
        if (tunnel.Peers.Count == 0)
        {
            yield return new TextNode("No other Tailscale devices online", theme.Text, theme.Text.MutedColor);
            yield break;
        }

        foreach (var peer in tunnel.Peers.Take(8))
        {
            yield return BuildTailscalePeerRow(peer);
        }

        if (tunnel.Peers.Count > 8)
        {
            yield return new TextNode($"+{tunnel.Peers.Count - 8} more devices", theme.Text, theme.Text.MutedColor);
        }
    }

    private BoxNode BuildTailscalePeerRow(TailscalePeerSnapshot peer)
    {
        var ipAddress = peer.IpAddresses.FirstOrDefault();
        var state = _rowStates.GetState($"tailscale:{peer.Name}", theme.Panel).UpdateColor(theme.Panel);
        var icon = peer.OperatingSystem.Equals("android", StringComparison.OrdinalIgnoreCase) ||
                   peer.OperatingSystem.Equals("ios", StringComparison.OrdinalIgnoreCase)
            ? Icons.Smartphone
            : Icons.Laptop;

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            IsHovered = state.Hovered,
            OnClick = ipAddress is null ? null : () => _ = clipboard.CopyTextAsync(ipAddress),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
                Padding = new Insets(7, 6),
                Spacing = 3,
            },
            Children =
            [
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Children =
                    [
                        ModulesCommon.BuildTextWithIcon(theme, icon, peer.Name, maxTextWidth: 205),
                        new TextNode(
                            string.IsNullOrWhiteSpace(peer.OperatingSystem) ? "Online" : peer.OperatingSystem,
                            theme.Text,
                            theme.Text.MutedColor),
                    ],
                },
                new TextNode(ipAddress ?? "No Tailscale IP", theme.Text.SmallSize, theme.Text.MutedColor),
            ],
        };
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

    private BoxNode BuildDetailRow(string label, string value) => new(Style.Spacer)
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            new TextNode(label, theme.Text, theme.Text),
            new TextNode(string.IsNullOrWhiteSpace(value) ? "Unavailable" : value, theme.Text, theme.Text.MutedColor),
        ],
    };

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
                : () => service.ConnectWifiAsync(wifi.Ssid, null, CancellationToken.None),
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
                    UnselectedColor = theme.Text.MutedColor,
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
            OnClick = () => _ = clipboard.CopyTextAsync(ipAddress),
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
            Children = [new TextNode(text, 14.0f, theme.Text.MutedColor)],
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
            _wifiNetworks = await service.ScanWifiNetworksAsync(CancellationToken.None);
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
        _ = service.SetWifiEnabledAsync(enabled, CancellationToken.None);
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

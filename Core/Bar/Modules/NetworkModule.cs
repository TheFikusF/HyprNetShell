using HyprNetShell.GUI;
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
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private const string WIFI_POPUP_TAB = "wifi";
    private const string VPN_POPUP_TAB = "vpn";
    private const string DETAILS_POPUP_TAB = "details";

    private static readonly TimeSpan WifiScanInterval = TimeSpan.FromSeconds(5);

    private readonly ModulesCommon.BoxState _settingsState = new();
    private readonly SegmentedSwitch _popupTabSwitch = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private readonly Ref<float> _wifiSwitchAnimation = new();
    private IReadOnlyList<WifiNetworkSnapshot> _wifiNetworks = [];
    private DateTime _lastWifiScan = DateTime.MinValue;
    private Task? _wifiScanTask;
    private Task<WifiOperationResult>? _tailscaleOffTask;
    private bool? _wifiEnabledOverride;
    private string _selectedPopupTab = WIFI_POPUP_TAB;

    private readonly NodeWithPopup _node = new(popupCoordinator, "network_module") {
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
                Children = [new ImageNode(Icons.WifiStrength[^1], 18, 18, ThemeManager.Current.Text.Color with { A = 0.3f })]
            },
            new BoxNode
            {
                IgnoreLayout = true, Children = [new ImageNode(Icons.WifiStrength[strength], 18, 18, ThemeManager.Current.Text)]
            }
        };
    }

    private BoxNode BuildStateModule(NetworkSnapshot network)
    {
        Node icon = !network.Connected
            ? new ImageNode(Icons.WifiOff, 18, 18, ThemeManager.Current.Text)
            : network.Type.Equals("wifi", StringComparison.OrdinalIgnoreCase)
                ? WifiIcon(WifiStrengthIndex(network.WifiSignal), 18)
                : network.Type.Equals("ethernet", StringComparison.OrdinalIgnoreCase)
                    ? new ImageNode(Icons.Ethernet, 18, 18, ThemeManager.Current.Text)
                    : new ImageNode(Icons.Globe, 18, 18, ThemeManager.Current.Text);

        var background = ModulesCommon.ToBackground(Color.Lerp(Color.Green, Color.Blue, 0.3f));
        return new BoxNode {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(background, left: false) with {
                ShadowColor = null
            },
            Children = [icon],
        };
    }

    private BoxNode BuildPopup(NetworkSnapshot network) => new BoxNode(360) {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.PopupStyle(),
        Children =
        [
            _popupTabSwitch.Build([
                    new SegmentedSwitch.Item(WIFI_POPUP_TAB, new TextNode("Wi-Fi")),
                    new SegmentedSwitch.Item(VPN_POPUP_TAB, new TextNode("VPN")),
                    new SegmentedSwitch.Item(DETAILS_POPUP_TAB, new TextNode("Details")),
                ],
                _selectedPopupTab,
                selected => _selectedPopupTab = selected),
            ..BuildSelectedPopupTab(network),
        ]
    };

    private IReadOnlyList<Node> BuildSelectedPopupTab(NetworkSnapshot network) => _selectedPopupTab switch {
        VPN_POPUP_TAB =>
                [
                    ..BuildTunnelRows(network.Tunnels),
                    ..BuildTailscaleOperationFeedback(),
                ],
        DETAILS_POPUP_TAB =>
        [
            BuildDetailRow("Status", network.Connected ? "Connected" : "Disconnected"),
            BuildDetailRow("Device", network.Device),
            BuildDetailRow("Connection", network.Connection),
            BuildDetailRow("Type", network.Type),
            ..BuildIpRows(network),
        ],
        _ => [BuildWifiPowerRow(network), .. BuildWifiRows(EffectiveWifiEnabled(network))],
    };

    private void OpenWifiSettings()
    {
        _node.ClosePopup();
        dialogs.Open<CompositeWindow>([tabs.Get<WifiTab>()]);
    }

    private BoxNode BuildWifiPowerRow(NetworkSnapshot network)
    {
        var enabled = EffectiveWifiEnabled(network);
        _settingsState.UpdateColor(ThemeManager.Current.Panel);
        return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style() {
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new BoxNode(48 + 8 + 20 + 8),
                ModulesCommon.BuildTextWithIcon(Icons.WifiStrength[^1], "Wi-Fi"),
                new BoxNode(Style.Spacer, ItemsAlignment.Center, ItemsAlignment.Center)
                {
                    new BoxNode()
                    {
                        HorizontalAlignment = ItemsAlignment.Spread,
                        VerticalAlignment = ItemsAlignment.Center,
                        IsHovered = _settingsState.Hovered,
                        OnClick = network.WifiAvailable ? OpenWifiSettings : null,
                        Style = ModulesCommon.ModuleStyle(_settingsState.Background) with
                        {
                            Padding = 4,
                            BorderRadius = 8,
                            BorderWidth = 0,
                        },
                        Children = [new ImageNode(Icons.Settings, 20, 20, ThemeManager.Current.Text)]
                    },
                    new BoxNode()
                    {
                        OnClick = network.WifiAvailable ? () => SetWifiEnabled(network, !enabled) : null,
                        Children =
                        [
                            new SwitchNode(enabled, _wifiSwitchAnimation)
                            {
                                OffTrackColor = ThemeManager.Current.Text.MutedColor,
                                OnTrackColor = ThemeManager.Current.Active,
                                KnobColor = ThemeManager.Current.Text,
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

    private BoxNode BuildTunnelRow(NetworkTunnelSnapshot tunnel) => new() {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with {
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
                    ModulesCommon.BuildTextWithIcon(tunnel.IsTailscale ? Icons.Globe : Icons.Lock,
                        tunnel.Name,
                        maxTextWidth: 220),
                    tunnel.IsTailscale
                        ? new BoxNode
                        {
                            OnClick = _tailscaleOffTask is { IsCompleted: false }
                                ? null
                                : () => _tailscaleOffTask = service.TurnOffTailscaleAsync(),
                            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with { Padding = 4, BorderRadius = 6 },
                            Children = [new TextNode(_tailscaleOffTask is { IsCompleted: false } ? "Turning off..." : "Turn off")],
                        }
                        : new TextNode("Active", color: ThemeManager.Current.Text.MutedColor),
                ],
            },
            BuildDetailRow("Device", tunnel.Device),
            BuildDetailRow("Type", tunnel.Type),
            ..tunnel.IpAddresses.Select(BuildIpRow),
            ..BuildTailscalePeerRows(tunnel),
        ],
    };

    private IEnumerable<Node> BuildTailscaleOperationFeedback()
    {
        if (_tailscaleOffTask is { IsCompletedSuccessfully: true } task && !task.Result.Success)
        {
            yield return new TextNode(task.Result.Error ?? "Could not turn off Tailscale", ThemeManager.Current.Text.SmallSize, ThemeManager.Current.Text.MutedColor);
        }
    }

    private IEnumerable<Node> BuildTailscalePeerRows(NetworkTunnelSnapshot tunnel)
    {
        if (!tunnel.IsTailscale)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(ThemeManager.Current.Border, height: 12);
        yield return BuildDetailRow("Devices online", tunnel.Peers.Count.ToString());
        if (tunnel.Peers.Count == 0)
        {
            yield return new TextNode("No other Tailscale devices online", color: ThemeManager.Current.Text.MutedColor);
            yield break;
        }

        foreach (var peer in tunnel.Peers.Take(8))
        {
            yield return BuildTailscalePeerRow(peer);
        }

        if (tunnel.Peers.Count > 8)
        {
            yield return new TextNode($"+{tunnel.Peers.Count - 8} more devices", color: ThemeManager.Current.Text.MutedColor);
        }
    }

    private BoxNode BuildTailscalePeerRow(TailscalePeerSnapshot peer)
    {
        var ipAddress = peer.IpAddresses.FirstOrDefault();
        var state = _rowStates.GetState($"tailscale:{peer.Name}", ThemeManager.Current.Panel).UpdateColor(ThemeManager.Current.Panel);
        var icon = peer.OperatingSystem.Equals("android", StringComparison.OrdinalIgnoreCase) ||
                   peer.OperatingSystem.Equals("ios", StringComparison.OrdinalIgnoreCase)
            ? Icons.Smartphone
            : Icons.Laptop;

        return new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            IsHovered = state.Hovered,
            OnClick = ipAddress is null ? null : () => _ = clipboard.CopyTextAsync(ipAddress),
            Style = ModulesCommon.ModuleStyle(state.Background) with {
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
                        ModulesCommon.BuildTextWithIcon(icon, peer.Name, maxTextWidth: 205),
                        new TextNode(string.IsNullOrWhiteSpace(peer.OperatingSystem) ? "Online" : peer.OperatingSystem, color: ThemeManager.Current.Text.MutedColor),
                    ],
                },
                new TextNode(ipAddress ?? "No Tailscale IP", ThemeManager.Current.Text.SmallSize, ThemeManager.Current.Text.MutedColor),
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

    private BoxNode BuildDetailRow(string label, string value) => new(Style.Spacer) {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            new TextNode(label),
            new TextNode(string.IsNullOrWhiteSpace(value) ? "Unavailable" : value, color: ThemeManager.Current.Text.MutedColor),
        ],
    };

    private BoxNode BuildWifiRow(WifiNetworkSnapshot wifi)
    {
        var state = _rowStates.GetState($"wifi:{wifi.Ssid}", ThemeManager.Current.Panel).UpdateColor(ThemeManager.Current.Panel);
        var ssid = string.IsNullOrWhiteSpace(wifi.Ssid) ? "<hidden>" : wifi.Ssid;
        var security = string.IsNullOrWhiteSpace(wifi.Security) ? "open" : wifi.Security;
        return new BoxNode {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = wifi.Active || string.IsNullOrWhiteSpace(wifi.Ssid)
                ? null
                : () => service.ConnectWifiAsync(wifi.Ssid, null, CancellationToken.None),
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Spacing = 12,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new RadioButtonNode(wifi.Active)
                {
                    SelectedColor = Color.Orange,
                    UnselectedColor = ThemeManager.Current.Text.MutedColor,
                    BackgroundColor = ThemeManager.Current.Panel,
                },
                WifiIcon(WifiStrengthIndex(wifi.Signal), 18),
                new TextNode(Trim(ssid, 22)),
                new TextNode(security),
            ],
        };
    }

    private BoxNode BuildIpRow(string ipAddress)
    {
        var state = _rowStates.GetState($"ip:{ipAddress}", ThemeManager.Current.Panel).UpdateColor(ThemeManager.Current.Panel);
        return new BoxNode {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => _ = clipboard.CopyTextAsync(ipAddress),
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Spacing = 8,
                BorderRadius = 8
            },
            Children =
            [
                new ImageNode(Icons.Copy, 14, 14, ThemeManager.Current.Text),
                new TextNode(ipAddress),
            ],
        };
    }

    private Node BuildPlainRow(string text) =>
        new BoxNode {
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with { BorderRadius = 8 },
            Children = [new TextNode(text, color: ThemeManager.Current.Text.MutedColor)],
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

    private static int WifiStrengthIndex(int? signal) => signal switch {
        null or <= 25 => 0,
        <= 50 => 1,
        <= 75 => 2,
        _ => 3,
    };

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
}

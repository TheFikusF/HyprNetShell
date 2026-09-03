using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using QRCoder;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class WifiTab(NetworkModuleService service, Theme theme) : IMainDialogTab, IDisposable
{
    private const int VisibleNetworkCount = 7;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(5);

    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private readonly Ref<float> _wifiSwitchAnimation = new();
    private IReadOnlyList<WifiNetworkSnapshot> _networks = [];
    private Task? _scanTask;
    private Task? _operationTask;
    private Task? _shareTask;
    private DateTime _lastScanUtc = DateTime.MinValue;
    private WifiNetworkSnapshot? _passwordNetwork;
    private WifiNetworkSnapshot? _shareNetwork;
    private RawImageData? _qrImage;
    private string _password = "";
    private string? _status;
    private int _firstIndex;
    private int _selectedIndex;
    private bool? _wifiEnabledOverride;
    private bool _disposed;

    public string Id => "wifi";
    public string Title => "Wi-Fi";
    public SvgAsset Icon => Icons.WifiStrength[^1];

    public void Activate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_stateLock)
        {
            _passwordNetwork = null;
            _shareNetwork = null;
            _qrImage = null;
            _password = "";
            _status = null;
        }

        ScheduleScan(force: true);
    }

    public void HandleTextInput(string text)
    {
        lock (_stateLock)
        {
            if (_passwordNetwork is not null)
            {
                _password += text;
            }
        }
    }

    public void HandleBackspace()
    {
        lock (_stateLock)
        {
            if (_passwordNetwork is not null)
            {
                _password = MainDialogTabUi.RemoveLastTextElement(_password);
            }
        }
    }

    public bool HandleEscape()
    {
        bool cancelShare;
        bool cancelPassword;
        lock (_stateLock)
        {
            cancelShare = _shareNetwork is not null;
            cancelPassword = _passwordNetwork is not null;
        }

        if (cancelShare)
        {
            CancelShare();
            return true;
        }

        if (cancelPassword)
        {
            CancelPassword();
            return true;
        }

        return false;
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction == SelectionDirection.Up)
        {
            MoveSelection(-1);
        }
        else if (direction == SelectionDirection.Down)
        {
            MoveSelection(1);
        }
    }

    public void ActivateSelection()
    {
        WifiNetworkSnapshot? selected;
        lock (_stateLock)
        {
            if (_shareNetwork is not null)
            {
                return;
            }

            if (_passwordNetwork is not null)
            {
                selected = null;
            }
            else
            {
                selected = _selectedIndex >= 0 && _selectedIndex < _networks.Count
                    ? _networks[_selectedIndex]
                    : null;
            }
        }

        if (selected is not null)
        {
            BeginConnect(selected);
        }
        else
        {
            ConnectWithPassword();
        }
    }

    public Node Draw()
    {
        var network = service.Snapshot;
        var wifiEnabled = EffectiveWifiEnabled(network);
        if (wifiEnabled)
        {
            ScheduleScan(force: false);
        }

        IReadOnlyList<WifiNetworkSnapshot> networks;
        WifiNetworkSnapshot? passwordNetwork;
        WifiNetworkSnapshot? shareNetwork;
        RawImageData? qrImage;
        string password;
        string? status;
        int firstIndex;
        bool busy;
        lock (_stateLock)
        {
            networks = _networks;
            passwordNetwork = _passwordNetwork;
            shareNetwork = _shareNetwork;
            qrImage = _qrImage;
            password = _password;
            status = _status;
            firstIndex = _firstIndex;
            busy = _operationTask is { IsCompleted: false } || _shareTask is { IsCompleted: false };
        }

        return new BoxNode()
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = new Style { Spacing = 12 },
            Children = shareNetwork is not null
                ? [BuildSharePrompt(shareNetwork, qrImage), BuildStatus(status)]
                : passwordNetwork is not null
                    ? [BuildPasswordPrompt(passwordNetwork, password, busy), BuildStatus(status)]
                    : [BuildHeader(network, wifiEnabled), .. BuildNetworks(wifiEnabled, networks, firstIndex, busy), BuildStatus(status)],
        };
    }

    private BoxNode BuildHeader(NetworkSnapshot network, bool enabled) => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            new TextNode(network.Connected && network.Type.Equals("wifi", StringComparison.OrdinalIgnoreCase)
                ? $"Connected to {network.Connection}"
                : "Choose a wireless network", theme.Text.HeaderSize, theme.Text),
            new BoxNode
            {
                OnClick = network.WifiAvailable ? () => SetWifiEnabled(!enabled) : null,
                VerticalAlignment = ItemsAlignment.Center,
                Style = Style.Spacer,
                Children =
                [
                    new TextNode(enabled ? "On" : "Off", theme.Text, theme.Text.MutedColor),
                    new SwitchNode(enabled, _wifiSwitchAnimation)
                    {
                        OffTrackColor = theme.Text.MutedColor,
                        OnTrackColor = theme.Active,
                        KnobColor = theme.Text,
                    },
                ],
            },
        ],
    };

    private IEnumerable<Node> BuildNetworks(
        bool enabled,
        IReadOnlyList<WifiNetworkSnapshot> networks,
        int firstIndex,
        bool busy)
    {
        yield return ModulesCommon.BuildDivider(theme.Border, height: 12);
        if (!enabled)
        {
            yield return BuildMessage("Wi-Fi is turned off");
            yield break;
        }

        if (_scanTask is { IsCompleted: false } && networks.Count == 0)
        {
            yield return BuildMessage("Scanning for networks...");
            yield break;
        }

        if (networks.Count == 0)
        {
            yield return BuildMessage("No wireless networks found");
            yield break;
        }

        var content = new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children = networks
                .VisibleItems(firstIndex, VisibleNetworkCount)
                .Select(item => BuildNetworkRow(item.Item, item.Index, busy))
                .ToArray(),
        };
        yield return BoundedListUi.BuildScrollableResults(
            content,
            firstIndex,
            networks.Count,
            VisibleNetworkCount,
            theme);
    }

    private BoxNode BuildNetworkRow(WifiNetworkSnapshot network, int index, bool busy)
    {
        var rowState = _rowStates.GetState(network.Ssid, theme.Panel);
        var selected = index == _selectedIndex;
        var baseColor = selected ? Color.Lighten(theme.Panel, 0.1f) : theme.Panel;
        rowState.UpdateColor(baseColor);
        var security = IsSecured(network) ? network.Security : "Open";

        return new BoxNode()
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = rowState.Hovered,
            OnClick = () => _selectedIndex = index,
            Style = ModulesCommon.ModuleStyle(theme, rowState.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.Border.Width : 0,
                Spacing = 8,
            },
            Children =
            [
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new RadioButtonNode(network.Active)
                    {
                        SelectedColor = Color.Orange,
                        UnselectedColor = theme.Text.MutedColor,
                        BackgroundColor = theme.Panel,
                    },
                    WifiIcon(network.Signal),
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        Style = new Style { Spacing = 2 },
                        Children =
                        [
                            new TextNode(network.Ssid, theme.Text, theme.Text, maxWidth: 310),
                            new TextNode(network.SavedConnectionName is null ? security : $"{security} · Saved", 12, theme.Text.MutedColor),
                        ],
                    },
                },
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    BuildButton(network.Active ? "Connected" : "Connect", $"connect:{network.Ssid}",
                        network.Active || busy ? null : () => BeginConnect(network)),
                    BuildButton("Forget", $"forget:{network.Ssid}",
                        network.SavedConnectionName is null || busy ? null : () => BeginForget(network)),
                    BuildIconButton(Icons.QrCode, $"share:{network.Ssid}",
                        busy || IsSecured(network) && network.SavedConnectionName is null ? null : () => BeginShare(network)),
                },
            ],
        };
    }

    private BoxNode BuildSharePrompt(WifiNetworkSnapshot network, RawImageData? qrImage) => new BoxNode
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Center,
        Style = new Style { Spacing = 14 },
        Children =
        [
            ModulesCommon.BuildTextWithIcon(theme, Icons.QrCode, $"Share {network.Ssid}", maxTextWidth: 360),
            new TextNode("Scan to connect to this Wi-Fi network", theme.Text, theme.Text.MutedColor),
            qrImage is null
                ? BuildMessage("Reading network credentials...")
                : new BoxNode
                {
                    Style = new Style { BackgroundColor = Color.White, Padding = 12, BorderRadius = 8 },
                    Children = [new ImageNode(qrImage, 320, 320)],
                },
            BuildButton("Back", "share-back", CancelShare),
        ],
    };

    private BoxNode BuildPasswordPrompt(WifiNetworkSnapshot network, string password, bool busy) => new ()
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 14 },
        Children =
        [
            ModulesCommon.BuildTextWithIcon(theme, Icons.Lock, $"Connect to {network.Ssid}", maxTextWidth: 360),
            new TextNode("Enter the network password", theme.Text, theme.Text.MutedColor),
            MainDialogTabUi.BuildInput(new string('•', password.Length), "Password"),
            new BoxNode
            {
                HorizontalAlignment = ItemsAlignment.End,
                Style = new Style { Spacing = 8 },
                Children =
                [
                    BuildButton("Cancel", "password-cancel", busy ? null : CancelPassword),
                    BuildButton(busy ? "Connecting..." : "Connect", "password-connect",
                        busy || password.Length == 0 ? null : ConnectWithPassword),
                ],
            },
        ],
    };

    private BoxNode BuildButton(string text, string key, Action? action) =>
        MainDialogTabUi.BuildButton(theme, _buttonStates, text, key, action);

    private BoxNode BuildIconButton(SvgAsset icon, string key, Action? action)
    {
        var state = _buttonStates.GetState(key, theme.Panel).UpdateColor(theme.Panel);
        return new (34, 34)
        {
            IsHovered = state.Hovered,
            OnClick = action,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 7,
                BorderWidth = 0,
                Padding = 6,
            },
            Children = [new ImageNode(icon, 16, 16, action is null ? theme.Text.MutedColor : theme.Text)],
        };
    }

    private Node BuildStatus(string? status) => MainDialogTabUi.BuildStatus(theme, status);

    private BoxNode BuildMessage(string message) => MainDialogTabUi.BuildMessage(theme, message);

    private ImageNode WifiIcon(int? signal) => new (Icons.WifiStrength[signal switch
    {
        null or <= 25 => 0,
        <= 50 => 1,
        <= 75 => 2,
        _ => 3,
    }], 18, 18, theme.Text);



    private void MoveSelection(int direction)
    {
        lock (_stateLock)
        {
            BoundedListUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction,
                _networks.Count,
                VisibleNetworkCount);
        }
    }

    private void BeginConnect(WifiNetworkSnapshot network)
    {
        if (network.Active)
        {
            return;
        }

        if (network.SavedConnectionName is null && IsSecured(network))
        {
            lock (_stateLock)
            {
                _passwordNetwork = network;
                _password = "";
                _status = null;
            }
            return;
        }

        StartOperation(
            cancellationToken => service.ConnectWifiAsync(network.Ssid, null, cancellationToken),
            $"Connecting to {network.Ssid}...");
    }

    private void ConnectWithPassword()
    {
        WifiNetworkSnapshot? network;
        string password;
        lock (_stateLock)
        {
            if (_operationTask is { IsCompleted: false })
            {
                return;
            }

            network = _passwordNetwork;
            password = _password;
        }

        if (network is null || password.Length == 0)
        {
            return;
        }

        StartOperation(
            cancellationToken => service.ConnectWifiAsync(network.Ssid, password, cancellationToken),
            $"Connecting to {network.Ssid}...",
            clearPasswordOnSuccess: true);
    }

    private void BeginForget(WifiNetworkSnapshot network)
    {
        if (network.SavedConnectionName is not { } connectionName)
        {
            return;
        }

        StartOperation(
            cancellationToken => service.ForgetWifiAsync(connectionName, cancellationToken),
            $"Forgetting {network.Ssid}...");
    }

    private void BeginShare(WifiNetworkSnapshot network)
    {
        lock (_stateLock)
        {
            if (_shareTask is { IsCompleted: false })
            {
                return;
            }

            _shareNetwork = network;
            _qrImage = null;
            _status = null;
            if (!IsSecured(network))
            {
                _qrImage = CreateQrImage(CreateWifiPayload(network, ""));
                return;
            }

            if (network.SavedConnectionName is not { } connectionName)
            {
                _status = "This network has no saved password";
                return;
            }

            _shareTask = LoadShareAsync(network, connectionName);
        }
    }

    private async Task LoadShareAsync(WifiNetworkSnapshot network, string connectionName)
    {
        var result = await service.ReadWifiPasswordAsync(connectionName, _lifetime.Token);
        var qrImage = result.Success && result.Password is not null
            ? CreateQrImage(CreateWifiPayload(network, result.Password))
            : null;
        lock (_stateLock)
        {
            _qrImage = qrImage;
            _status = result.Success ? null : result.Error ?? "Could not read the saved password";
        }
    }

    private void CancelShare()
    {
        lock (_stateLock)
        {
            _shareNetwork = null;
            _qrImage = null;
            _status = null;
        }
    }

    private static RawImageData CreateQrImage(string payload)
    {
        const int quietZoneModules = 4;
        const int pixelsPerModule = 8;

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var moduleCount = data.ModuleMatrix.Count;
        var imageSize = (moduleCount + quietZoneModules * 2) * pixelsPerModule;
        var pixels = new byte[imageSize * imageSize * 4];
        Array.Fill(pixels, byte.MaxValue);

        for (var moduleY = 0; moduleY < moduleCount; moduleY++)
        {
            for (var moduleX = 0; moduleX < moduleCount; moduleX++)
            {
                if (!data.ModuleMatrix[moduleY][moduleX])
                {
                    continue;
                }

                var startX = (moduleX + quietZoneModules) * pixelsPerModule;
                var startY = (moduleY + quietZoneModules) * pixelsPerModule;
                for (var pixelY = 0; pixelY < pixelsPerModule; pixelY++)
                {
                    for (var pixelX = 0; pixelX < pixelsPerModule; pixelX++)
                    {
                        var offset = ((startY + pixelY) * imageSize + startX + pixelX) * 4;
                        pixels[offset] = 0;
                        pixels[offset + 1] = 0;
                        pixels[offset + 2] = 0;
                    }
                }
            }
        }

        return new RawImageData(imageSize, imageSize, pixels);
    }

    private static string CreateWifiPayload(WifiNetworkSnapshot network, string password)
    {
        var authentication = !IsSecured(network)
            ? PayloadGenerator.WiFi.Authentication.nopass
            : network.Security.Contains("WEP", StringComparison.OrdinalIgnoreCase)
                ? PayloadGenerator.WiFi.Authentication.WEP
                : PayloadGenerator.WiFi.Authentication.WPA;
        return new PayloadGenerator.WiFi(network.Ssid, password, authentication).ToString();
    }

    private void SetWifiEnabled(bool enabled)
    {
        _wifiEnabledOverride = enabled;
        if (!enabled)
        {
            lock (_stateLock)
            {
                _networks = [];
            }
        }

        StartOperation(
            cancellationToken => service.SetWifiEnabledAsync(enabled, cancellationToken),
            $"Turning Wi-Fi {(enabled ? "on" : "off")}...");
    }

    private void StartOperation(
        Func<CancellationToken, Task<WifiOperationResult>> operation,
        string pendingStatus,
        bool clearPasswordOnSuccess = false)
    {
        lock (_stateLock)
        {
            if (_operationTask is { IsCompleted: false })
            {
                return;
            }

            _status = pendingStatus;
            _operationTask = RunOperationAsync(operation, clearPasswordOnSuccess);
        }
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task<WifiOperationResult>> operation,
        bool clearPasswordOnSuccess)
    {
        var result = await operation(_lifetime.Token);
        lock (_stateLock)
        {
            _status = result.Success ? "Done" : result.Error ?? "The Wi-Fi operation failed";
            if (result.Success && clearPasswordOnSuccess)
            {
                _passwordNetwork = null;
                _password = "";
            }
        }

        if (result.Success)
        {
            ScheduleScan(force: true);
        }
    }

    private void CancelPassword()
    {
        lock (_stateLock)
        {
            if (_operationTask is { IsCompleted: false })
            {
                return;
            }

            _passwordNetwork = null;
            _password = "";
            _status = null;
        }
    }

    private void ScheduleScan(bool force)
    {
        lock (_stateLock)
        {
            if (_disposed || _scanTask is { IsCompleted: false } || !force && DateTime.UtcNow - _lastScanUtc < ScanInterval)
            {
                return;
            }

            _lastScanUtc = DateTime.UtcNow;
            _scanTask = ScanAsync();
        }
    }

    private async Task ScanAsync()
    {
        var networks = await service.ScanWifiNetworksAsync(_lifetime.Token);
        if (networks.Count == 0 && !_lifetime.IsCancellationRequested)
        {
            networks = await service.ScanWifiNetworksAsync(_lifetime.Token);
        }

        lock (_stateLock)
        {
            _networks = networks;
            BoundedListUi.Normalize(
                ref _selectedIndex,
                ref _firstIndex,
                networks.Count,
                VisibleNetworkCount);
        }
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

    private static bool IsSecured(WifiNetworkSnapshot network) =>
        !string.IsNullOrWhiteSpace(network.Security) && network.Security != "--";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Task? scanTask;
        Task? operationTask;
        Task? shareTask;
        lock (_stateLock)
        {
            _disposed = true;
            scanTask = _scanTask;
            operationTask = _operationTask;
            shareTask = _shareTask;
        }

        _lifetime.Cancel();
        try
        {
            Task.WaitAll(
                [scanTask ?? Task.CompletedTask, operationTask ?? Task.CompletedTask, shareTask ?? Task.CompletedTask],
                TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _lifetime.Dispose();
    }
}

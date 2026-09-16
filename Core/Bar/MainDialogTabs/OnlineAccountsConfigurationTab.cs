using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.OnlineAccounts;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class OnlineAccountsConfigurationTab(OnlineAccountsService accounts, Theme theme) : IMainDialogTab
{
    private const int ClientIdMaxLength = 512;

    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private readonly Dictionary<OnlineAccountProvider, string> _clientIds = [];
    private OnlineAccountProvider? _editingProvider;

    public string Id => "online-accounts";
    public string Title => "Accounts";
    public SvgAsset Icon => Icons.Accounts;

    public void Activate()
    {
        accounts.EnsureInitialized();
        SynchronizeClientIds(accounts.Snapshot);
    }

    public void HandleTextInput(string text)
    {
        if (_editingProvider is not { } provider || string.IsNullOrEmpty(text))
        {
            return;
        }

        var value = _clientIds.GetValueOrDefault(provider, "");
        var filtered = new string(text.Where(character => !char.IsControl(character)).ToArray());
        var available = ClientIdMaxLength - value.Length;
        if (available > 0)
        {
            _clientIds[provider] = value + filtered[..Math.Min(available, filtered.Length)];
        }
    }

    public void HandleBackspace()
    {
        if (_editingProvider is { } provider && _clientIds.GetValueOrDefault(provider, "") is { Length: > 0 } value)
        {
            _clientIds[provider] = MainDialogTabUi.RemoveLastTextElement(value);
        }
    }

    public bool HandleEscape()
    {
        if (_editingProvider is null)
        {
            return false;
        }

        _editingProvider = null;
        return true;
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
        if (_editingProvider is { } provider)
        {
            SaveClientId(provider);
        }
    }

    public Node Draw()
    {
        accounts.EnsureInitialized();
        var snapshots = accounts.Snapshot;
        SynchronizeClientIds(snapshots);
        var connectedCount = snapshots.Count(snapshot => snapshot.Connected);
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 12 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Online accounts",
                    connectedCount == 0 ? "Credentials stored with Secret Service" : $"{connectedCount} connected"),
                new TextNode(
                    "Client ID priority: embedded build, environment variable, then saved configuration.",
                    theme.Text,
                    theme.Text.MutedColor,
                    maxWidth: 920),
                ..snapshots.Select(BuildProviderRow),
                new TextNode(
                    "ChatGPT sign-in mirrors Zed's current Codex OAuth integration. OpenAI does not publish it as a stable third-party API, so it may change.",
                    12,
                    theme.Text.MutedColor,
                    maxWidth: 920),
            ],
        };
    }

    private Node BuildProviderRow(OnlineAccountSnapshot snapshot)
    {
        var busy = accounts.BusyProvider;
        var isBusy = busy == snapshot.Provider;
        Action? connectionAction = snapshot.Connected
            ? busy is null ? () => accounts.Disconnect(snapshot.Provider) : null
            : snapshot.Configured && busy is null ? () => accounts.Connect(snapshot.Provider) : null;
        var connectionLabel = isBusy
            ? "Working…"
            : snapshot.Connected ? "Disconnect" : snapshot.Configured ? "Connect" : "No client ID";
        var accountStatus = snapshot.Status ?? (snapshot.Connected
            ? snapshot.AccountName ?? "Connected"
            : "Not connected");

        return new BoxNode(height: 126)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                Padding = new Insets(16, 10),
                BorderRadius = 8,
                BorderWidth = snapshot.Connected ? theme.Border.Width : 0,
            },
            Children =
            [
                new BoxNode
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 14 },
                    Children =
                    [
                        new ImageNode(ProviderIcon(snapshot.Provider), 28, 28, snapshot.Connected ? theme.Active : theme.Text),
                        new BoxNode
                        {
                            Direction = Direction.Vertical,
                            Style = new Style { Spacing = 4 },
                            Children =
                            [
                                new TextNode(snapshot.Name, 17, theme.Text),
                                new TextNode(snapshot.Description, 12, theme.Text.MutedColor, maxWidth: 600),
                                ..BuildClientIdInputNodes(snapshot),
                                new TextNode(
                                    $"{accountStatus} · {ClientIdSourceText(snapshot)}",
                                    12,
                                    snapshot.Connected ? theme.Active : theme.Text.MutedColor,
                                    maxWidth: 620),
                            ],
                        },
                    ],
                },
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    Style = new Style { Spacing = 6 },
                    Children =
                    [
                        ..BuildClientIdEditButtonNodes(snapshot, busy),
                        BuildButton(
                            connectionLabel,
                            "connect:" + snapshot.Provider,
                            connectionAction,
                            snapshot.Connected),
                    ],
                },
            ],
        };
    }

    private Node[] BuildClientIdInputNodes(OnlineAccountSnapshot snapshot) =>
        IsEmbeddedClientId(snapshot) ? [] : [BuildClientIdInput(snapshot)];

    private Node[] BuildClientIdEditButtonNodes(OnlineAccountSnapshot snapshot, OnlineAccountProvider? busy) =>
        IsEmbeddedClientId(snapshot)
            ? []
            :
            [
                BuildButton(
                    _editingProvider == snapshot.Provider ? "Save ID" : "Edit ID",
                    "edit:" + snapshot.Provider,
                    busy is null
                        ? () => ToggleClientIdEditor(snapshot.Provider)
                        : null),
            ];

    private Node BuildClientIdInput(OnlineAccountSnapshot snapshot)
    {
        var editing = _editingProvider == snapshot.Provider;
        var value = _clientIds.GetValueOrDefault(snapshot.Provider, "");
        var caret = editing && Math.Sin(Environment.TickCount64 / 200.0) > 0 ? "|" : "";
        var displayed = value.Length == 0 ? "Optional configuration client ID" : value + caret;
        return new BoxNode(width: 620, height: 28)
        {
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => _editingProvider = snapshot.Provider,
            Style = ModulesCommon.ModuleStyle(theme, editing ? theme.Active : theme.Panel) with
            {
                Padding = new Insets(8, 3),
                BorderRadius = 6,
                BorderWidth = editing ? theme.Border.Width : 0,
            },
            Children =
            [
                new TextNode(
                    displayed,
                    12,
                    value.Length == 0 ? theme.Text.MutedColor : theme.Text,
                    maxWidth: 600,
                    wrapping: TextWrapping.Ellipsis),
            ],
        };
    }

    private BoxNode BuildButton(string label, string key, Action? action, bool active = false)
    {
        var state = _buttonStates.GetState(key, theme.Panel).UpdateColor(active ? theme.Active : theme.Panel);
        if (action is null)
        {
            state.Hovered.Value = false;
        }

        return new BoxNode
        {
            OnClick = action,
            IsHovered = action is null ? null : state.Hovered,
            Opacity = action is null ? 0.55f : 1.0f,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = new Insets(12, 7),
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(label, 13, theme.Text)],
        };
    }

    private void ToggleClientIdEditor(OnlineAccountProvider provider)
    {
        if (_editingProvider == provider)
        {
            SaveClientId(provider);
        }
        else
        {
            _editingProvider = provider;
        }
    }

    private void SaveClientId(OnlineAccountProvider provider)
    {
        accounts.SetConfiguredClientId(provider, _clientIds.GetValueOrDefault(provider, ""));
        _editingProvider = null;
    }

    private void SynchronizeClientIds(IEnumerable<OnlineAccountSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (_editingProvider != snapshot.Provider)
            {
                _clientIds[snapshot.Provider] = snapshot.ConfiguredClientId;
            }
        }
    }

    private static bool IsEmbeddedClientId(OnlineAccountSnapshot snapshot) =>
        string.Equals(snapshot.ClientIdSource, "Embedded", StringComparison.Ordinal);

    private static string ClientIdSourceText(OnlineAccountSnapshot snapshot) => snapshot.ClientIdSource is { } source
        ? $"Client ID: {source}"
        : "Client ID not configured";

    private static SvgAsset ProviderIcon(OnlineAccountProvider provider) => provider switch
    {
        OnlineAccountProvider.Google => Icons.Calendar,
        OnlineAccountProvider.Spotify => Icons.MusicNotes[0],
        OnlineAccountProvider.ChatGpt => Icons.Bot,
        _ => Icons.Accounts,
    };
}

using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.OnlineAccounts;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class OnlineAccountsConfigurationTab(OnlineAccountsService accounts) : IMainDialogTab
{
    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];

    public string Id => "online-accounts";
    public string Title => "Accounts";
    public SvgAsset Icon => Icons.Accounts;

    public void Activate()
    {
        accounts.EnsureInitialized();
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
    }

    public Node Draw()
    {
        accounts.EnsureInitialized();
        var snapshots = accounts.Snapshot.Where(snapshot => snapshot.Configured).ToArray();
        var connectedCount = snapshots.Count(snapshot => snapshot.Connected);
        return new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 12 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Online accounts",
                    connectedCount == 0 ? "Credentials stored with Secret Service" : $"{connectedCount} connected"),
                ..snapshots.Select(BuildProviderRow)
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

        return new BoxNode() {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with {
                BorderRadius = 8,
                BorderWidth = snapshot.Connected ? ThemeManager.Current.Border.Width : 0,
                Padding = new Insets(12, 12)
            },
            Children =
            [
                new BoxNode
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 12 },
                    Children =
                    [
                        new ImageNode(ProviderIcon(snapshot.Provider), 28, 28, snapshot.Connected ? ThemeManager.Current.Active : ThemeManager.Current.Text),
                        new BoxNode
                        {
                            Direction = Direction.Vertical,
                            Style = new Style { Spacing = 4 },
                            Children =
                            [
                                new TextNode(snapshot.Name, ThemeManager.Current.Text.HeaderSize),
                                new TextNode(snapshot.Description, color: ThemeManager.Current.Text.MutedColor, maxWidth: 600),
                                new TextNode(
                                    accountStatus,
                                    color: snapshot.Connected ? ThemeManager.Current.Active : ThemeManager.Current.Text.MutedColor,
                                    maxWidth: 620),
                            ],
                        },
                    ],
                },
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    Style = new Style { Spacing = 8 },
                    Children =
                    [
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


    private BoxNode BuildButton(string label, string key, Action? action, bool active = false)
    {
        var state = _buttonStates.GetState(key, ThemeManager.Current.Panel).UpdateColor(active ? ThemeManager.Current.Active : ThemeManager.Current.Panel);
        if (action is null)
        {
            state.Hovered.Value = false;
        }

        return new BoxNode {
            OnClick = action,
            IsHovered = action is null ? null : state.Hovered,
            Opacity = action is null ? 0.55f : 1.0f,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                Padding = new Insets(12, 8),
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(label)],
        };
    }


    private static SvgAsset ProviderIcon(OnlineAccountProvider provider) => provider switch {
        OnlineAccountProvider.Google => Icons.Calendar,
        OnlineAccountProvider.Spotify => Icons.MusicNotes[0],
        OnlineAccountProvider.ChatGpt => Icons.Bot,
        _ => Icons.Accounts,
    };
}

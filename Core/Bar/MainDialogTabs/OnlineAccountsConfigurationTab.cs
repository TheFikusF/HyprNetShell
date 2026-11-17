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
    private readonly Dictionary<string, ActionButton> _buttons = [];

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
        Action connectionAction = snapshot.Connected
            ? () => accounts.Disconnect(snapshot.Provider)
            : () => accounts.Connect(snapshot.Provider);
        var connectionLabel = isBusy
            ? "Working…"
            : snapshot.Connected ? "Disconnect" : snapshot.Configured ? "Connect" : "No client ID";
        var accountStatus = snapshot.Status ?? (snapshot.Connected
            ? snapshot.AccountName ?? "Connected"
            : "Not connected");

        var key = "connect:" + snapshot.Provider;
        if (!_buttons.TryGetValue(key, out var button))
        {
            button = new ActionButton();
            _buttons[key] = button;
        }

        button.Text = connectionLabel;
        button.Action = connectionAction;
        button.IsEnabled = busy is null && (snapshot.Connected || snapshot.Configured);
        button.Style = ActionButton.ButtonStyle.Default with {
            Background = snapshot.Connected ? ThemeManager.Current.Active : ThemeManager.Current.Panel,
            DisabledOpacity = 0.55f,
        };
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
                        button.Build(),
                    ],
                },
            ],
        };
    }




    private static SvgAsset ProviderIcon(OnlineAccountProvider provider) => provider switch {
        OnlineAccountProvider.Google => Icons.Calendar,
        OnlineAccountProvider.Spotify => Icons.MusicNotes[0],
        OnlineAccountProvider.ChatGpt => Icons.Bot,
        _ => Icons.Accounts,
    };
}

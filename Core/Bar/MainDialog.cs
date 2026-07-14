using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;

namespace HyprNetShell.Core.Bar;

public sealed class MainDialog : IDrawableModule
{
    private const int KEY_ESCAPE = 1;
    private const int KEY_BACKSPACE = 14;
    private const int KEY_TAB = 15;
    private const int KEY_ENTER = 28;
    private const int KEY_UP = 103;
    private const int KEY_LEFT = 105;
    private const int KEY_RIGHT = 106;
    private const int KEY_DOWN = 108;

    private readonly IMainDialogTab[] _tabs;

    private readonly IReadOnlyDictionary<int, Action> _actions;

    private int _activeTabIndex;
    private IMainDialogTab ActiveTab => _tabs[_activeTabIndex];

    public bool IsOpen { get; private set; }

    public MainDialog()
    {
        _tabs =
        [
            new ApplicationLauncherTab(Close),
            new CalculatorTab(),
            new ClipboardManagerTab(Close),
            new WallpapersTab(Close),
        ];

        _actions = new Dictionary<int, Action>
        {
            [KEY_ESCAPE] = Close,
            [KEY_BACKSPACE] = () => ActiveTab.HandleBackspace(),
            [KEY_ENTER] = () => ActiveTab.ActivateSelection(),
            [KEY_TAB] = () => SelectTab((_activeTabIndex + 1) % _tabs.Length),
            [KEY_UP] = () => ActiveTab.MoveSelection(SelectionDirection.Up),
            [KEY_LEFT] = () => ActiveTab.MoveSelection(SelectionDirection.Left),
            [KEY_RIGHT] = () => ActiveTab.MoveSelection(SelectionDirection.Right),
            [KEY_DOWN] = () => ActiveTab.MoveSelection(SelectionDirection.Down),
        };
    }

    public void Toggle()
    {
        Action action = IsOpen ? Close : Open;
        action();
    }

    public void Open()
    {
        _activeTabIndex = 0;
        ActiveTab.Activate();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void HandleInput(int pressedKey, string textInput, float scrollDelta)
    {
        if (!IsOpen)
        {
            return;
        }

        if (_actions.TryGetValue(pressedKey, out var action))
        {
            action();
            return;
        }

        if (!string.IsNullOrEmpty(textInput))
        {
            ActiveTab.HandleTextInput(textInput);
        }

        if (scrollDelta != 0)
        {
            ActiveTab.MoveSelection(scrollDelta > 0 ? SelectionDirection.Down : SelectionDirection.Up);
        }
    }

    public Node Draw() => new BoxNode(900)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.PopupStyle(Theme.Default) with
        {
            Padding = 24,
            Spacing = 8,
        },
        Children =
        [
            BuildTabs(),
            ActiveTab.Draw(),
        ],
    };

    private Node BuildTabs() => new BoxNode(height: 46)
    {
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 8 },
        Children = [.._tabs.Select(BuildTab)],
    };

    private Node BuildTab(IMainDialogTab tab)
    {
        var index = Array.IndexOf(_tabs, tab);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => SelectTab(index),
            Style = ModulesCommon.ModuleStyle(Theme.Default,
                    index == _activeTabIndex ? Theme.Default.Active : Theme.Default.Panel) with
                {
                    Spacing = 8,
                    BorderRadius = 8,
                    BorderWidth = index == _activeTabIndex ? Theme.Default.BorderWidth : 0,
                },
            Children =
            [
                new ImageNode(tab.Icon, 18, 18, Theme.Default.Text),
                new TextNode(tab.Title, 15, Theme.Default.Text),
            ],
        };
    }

    private void SelectTab(int index)
    {
        _activeTabIndex = index;
        ActiveTab.Activate();
    }
}

using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

public sealed class MainDialog : IDrawableModule
{
    private class Tab : IMainDialogTab
    {
        private readonly IMainDialogTab _tab;
        public ModulesCommon.BoxState BoxState { get; }

        public Tab(IMainDialogTab tab)
        {
            _tab = tab;
            BoxState = new();
        }

        public string Title => _tab.Title;
        public SvgAsset Icon => _tab.Icon;

        public void Activate() => _tab.Activate();

        public void HandleTextInput(string text) => _tab.HandleTextInput(text);

        public void HandleBackspace() => _tab.HandleBackspace();

        public void MoveSelection(SelectionDirection direction) => _tab.MoveSelection(direction);

        public void ActivateSelection() => _tab.ActivateSelection();

        public Node Draw() => _tab.Draw();
    }

    private const int KEY_ESCAPE = 1;
    private const int KEY_BACKSPACE = 14;
    private const int KEY_TAB = 15;
    private const int KEY_ENTER = 28;
    private const int KEY_UP = 103;
    private const int KEY_LEFT = 105;
    private const int KEY_RIGHT = 106;
    private const int KEY_DOWN = 108;

    private readonly Tab[] _tabs;
    private Theme _theme;

    private readonly IReadOnlyDictionary<int, Action> _actions;

    private int _activeTabIndex;
    private IMainDialogTab ActiveTab => _tabs[_activeTabIndex];

    public bool IsOpen { get; private set; }

    internal MainDialog(ClipboardHistoryService clipboardHistory, IHyprctl hyprctl, Theme theme)
    {
        _theme = theme;
        
        _tabs =
        [
            new Tab(new ApplicationLauncherTab(hyprctl, Close, theme)),
            new Tab(new CalculatorTab()),
            new Tab(new ClipboardManagerTab(clipboardHistory, Close, theme)),
            new Tab(new WallpapersTab(hyprctl, Close, theme)),
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
        Style = ModulesCommon.PopupStyle(_theme) with
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

    private Node BuildTab(Tab tab)
    {
        var index = Array.IndexOf(_tabs, tab);
        var normal = index == _activeTabIndex ? _theme.Active : _theme.Panel;
        var target = tab.BoxState.Hovered ? Color.Lighten(normal, index == _activeTabIndex ? 0.18f : 0.12f) : normal;
        tab.BoxState.Background = Color.LerpSmooth(tab.BoxState.Background, target, 18.0f, ModulesCommon.DELTA_TIME);
        
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => SelectTab(index),
            IsHovered = tab.BoxState.Hovered,
            Style = ModulesCommon.ModuleStyle(_theme, tab.BoxState.Background) with
            {
                Spacing = 8,
                BorderRadius = 8,
                BorderWidth = index == _activeTabIndex ? _theme.BorderWidth : 0,
            },
            Children =
            [
                new ImageNode(tab.Icon, 18, 18, _theme.Text),
                new TextNode(tab.Title, 15, _theme.Text),
            ],
        };
    }

    private void SelectTab(int index)
    {
        _activeTabIndex = index;
        ActiveTab.Activate();
    }
}
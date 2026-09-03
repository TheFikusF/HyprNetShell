using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;

using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal sealed class CompositeWindow : IDialogWindow
{
    private class Tab : IMainDialogTab
    {
        private readonly IMainDialogTab _tab;
        public ModulesCommon.BoxState BoxState { get; } = new();
        public IMainDialogTab InternalTab => _tab;

        public Tab(IMainDialogTab tab)
        {
            _tab = tab;
        }

        public string Id => _tab.Id;
        public string Title => _tab.Title;
        public SvgAsset Icon => _tab.Icon;

        public void Activate() => _tab.Activate();

        public bool HandleKey(DialogKey key) => _tab.HandleKey(key);

        public void HandleTextInput(string text) => _tab.HandleTextInput(text);

        public void HandleBackspace() => _tab.HandleBackspace();

        public bool HandleEscape() => _tab.HandleEscape();

        public void MoveSelection(SelectionDirection direction) => _tab.MoveSelection(direction);

        public void ActivateSelection() => _tab.ActivateSelection();

        public Node Draw() => _tab.Draw();
    }

    private Tab[] _tabs = [];
    private readonly Theme _theme;
    private readonly IReadOnlyDictionary<DialogKey, Action> _actions;

    private int _activeTabIndex;
    private IMainDialogTab ActiveTab => _tabs[_activeTabIndex];

    internal CompositeWindow(Theme theme)
    {
        _theme = theme;

        _actions = new Dictionary<DialogKey, Action>
        {
            [DialogKey.Backspace] = () => ActiveTab.HandleBackspace(),
            [DialogKey.Enter] = () => ActiveTab.ActivateSelection(),
            [DialogKey.Tab] = () => SelectTab((_activeTabIndex + 1) % _tabs.Length),
            [DialogKey.Up] = () => ActiveTab.MoveSelection(SelectionDirection.Up),
            [DialogKey.Left] = () => ActiveTab.MoveSelection(SelectionDirection.Left),
            [DialogKey.Right] = () => ActiveTab.MoveSelection(SelectionDirection.Right),
            [DialogKey.Down] = () => ActiveTab.MoveSelection(SelectionDirection.Down),
        };
    }

    internal void SetTabs(IReadOnlyList<IMainDialogTab> tabs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tabs.Count);
        _tabs = [..tabs.Select(tab => new Tab(tab))];
    }

    public void OnOpened()
    {
        _activeTabIndex = 0;
        ActiveTab.Activate();
    }

    public void OnClosed()
    {
    }

    public DialogInputResult HandleInput(DialogInput input)
    {
        if (ActiveTab.HandleKey(input.Key))
        {
            return DialogInputResult.None;
        }

        if (input.Key == DialogKey.Escape)
        {
            return ActiveTab.HandleEscape() ? DialogInputResult.None : DialogInputResult.Close;
        }

        if (_actions.TryGetValue(input.Key, out var action))
        {
            action();
            return DialogInputResult.None;
        }

        if (!string.IsNullOrEmpty(input.Text))
        {
            ActiveTab.HandleTextInput(input.Text);
        }

        if (input.ScrollDelta != 0)
        {
            ActiveTab.MoveSelection(input.ScrollDelta > 0 ? SelectionDirection.Down : SelectionDirection.Up);
        }

        return DialogInputResult.None;
    }

    public Node Draw() => new BoxNode(900)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.PopupStyle(_theme) with { Padding = 24, Spacing = 16 },
        Children = [BuildTabs(), ActiveTab.Draw()],
    };

    private BoxNode BuildTabs() => new(height: 46)
    {
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children = [.._tabs.Select(BuildTab)],
    };

    private Node BuildTab(Tab tab)
    {
        var index = Array.IndexOf(_tabs, tab);
        var normal = index == _activeTabIndex ? _theme.Active : _theme.Panel;
        var target = tab.BoxState.Hovered ? Color.Lighten(normal, index == _activeTabIndex ? 0.18f : 0.12f) : normal;
        tab.BoxState.Background = Color.LerpSmooth(tab.BoxState.Background, target, 18.0f, Renderer.DeltaTime);

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
                BorderWidth = index == _activeTabIndex ? _theme.Border.Width : 0,
            },
            Children = [new ImageNode(tab.Icon, 18, 18, _theme.Text), new TextNode(tab.Title, 15, _theme.Text)],
        };
    }

    private void SelectTab(int index)
    {
        _activeTabIndex = index;
        ActiveTab.Activate();
    }
}

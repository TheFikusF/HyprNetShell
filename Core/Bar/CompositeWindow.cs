using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;

using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;

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
        public bool HandleScroll => _tab.HandleScroll;

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
    private readonly TextInputCoordinator _textInputs;

    private readonly IReadOnlyDictionary<DialogKey, Action> _actions;

    private int _activeTabIndex;
    private IMainDialogTab ActiveTab => _tabs[_activeTabIndex];

    internal CompositeWindow(TextInputCoordinator textInputs)
    {
        _textInputs = textInputs;

        _actions = new Dictionary<DialogKey, Action> {
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
        _tabs = [.. tabs.Select(tab => new Tab(tab))];
    }

    public void OnOpened()
    {
        _activeTabIndex = 0;
        ActiveTab.Activate();
    }

    public void OnClosed()
    {
        _textInputs.Deactivate();
    }

    public DialogInputResult HandleInput(DialogInput input)
    {
        if (_textInputs.HandleKey(input.Key) || ActiveTab.HandleKey(input.Key))
        {
            return DialogInputResult.None;
        }

        if (input.Key == DialogKey.Escape)
        {
            return _textInputs.HandleEscape() || ActiveTab.HandleEscape()
                ? DialogInputResult.None
                : DialogInputResult.Close;
        }

        if (input.Key == DialogKey.Backspace)
        {
            if (!_textInputs.HandleBackspace(input.ControlPressed))
            {
                ActiveTab.HandleBackspace();
            }
            return DialogInputResult.None;
        }

        if (input.Key == DialogKey.Enter)
        {
            if (!_textInputs.HandleEnter())
            {
                ActiveTab.ActivateSelection();
            }
            return DialogInputResult.None;
        }

        if (_actions.TryGetValue(input.Key, out var action))
        {
            action();
            return DialogInputResult.None;
        }

        if (!string.IsNullOrEmpty(input.Text) && !_textInputs.HandleTextInput(input.Text))
        {
            ActiveTab.HandleTextInput(input.Text);
        }

        if (ActiveTab.HandleScroll && input.ScrollDelta != 0)
        {
            ActiveTab.MoveSelection(input.ScrollDelta > 0 ? SelectionDirection.Down : SelectionDirection.Up);
        }

        return DialogInputResult.None;
    }

    public Node Draw() => new BoxNode(900) {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.PopupStyle() with { Padding = 24, Spacing = 16 },
        Children = [BuildTabs(), ActiveTab.Draw()],
    };

    private BoxNode BuildTabs() => new(height: 46) {
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Stretch,
        Style = Style.Spacer,
        Children = [.. _tabs.Select(BuildTab)],
    };

    private Node BuildTab(Tab tab)
    {
        var index = Array.IndexOf(_tabs, tab);
        var normal = index == _activeTabIndex ? ThemeManager.Current.Active : ThemeManager.Current.Panel;
        var target = tab.BoxState.Hovered ? Color.Lighten(normal, index == _activeTabIndex ? 0.18f : 0.12f) : normal;
        tab.BoxState.Background = Color.LerpSmooth(tab.BoxState.Background, target, 18.0f, Renderer.DeltaTime);

        return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => SelectTab(index),
            IsHovered = tab.BoxState.Hovered,
            Style = ModulesCommon.ModuleStyle(tab.BoxState.Background) with {
                Spacing = 8,
                BorderRadius = 8,
                BorderWidth = index == _activeTabIndex ? ThemeManager.Current.Border.Width : 0,
            },
            Children = [new ImageNode(tab.Icon, 18, 18, ThemeManager.Current.Text), new TextNode(tab.Title, 15)],
        };
    }

    private void SelectTab(int index)
    {
        _textInputs.Deactivate();
        _activeTabIndex = index;
        ActiveTab.Activate();
    }
}

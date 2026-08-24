using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal sealed class MainDialog : IDialogWindow, IDisposable
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

        public string Title => _tab.Title;
        public SvgAsset Icon => _tab.Icon;

        public void Activate() => _tab.Activate();

        public void HandleTextInput(string text) => _tab.HandleTextInput(text);

        public void HandleBackspace() => _tab.HandleBackspace();

        public void MoveSelection(SelectionDirection direction) => _tab.MoveSelection(direction);

        public void ActivateSelection() => _tab.ActivateSelection();

        public Node Draw() => _tab.Draw();
    }

    private readonly Tab[] _tabs;
    private readonly Theme _theme;
    private readonly IReadOnlyDictionary<DialogKey, Action> _actions;

    private int _activeTabIndex;
    private IMainDialogTab ActiveTab => _tabs[_activeTabIndex];

    internal MainDialog(
        ClipboardHistoryService clipboardHistory,
        IHyprctl hyprctl,
        WallpaperModuleService wallpapers,
        Action closeDialog,
        Theme theme)
    {
        _theme = theme;

        _tabs =
        [
            new Tab(new ApplicationLauncherTab(hyprctl, closeDialog, theme)),
            new Tab(new CalculatorTab()),
            new Tab(new ClipboardManagerTab(clipboardHistory, closeDialog, theme)),
            new Tab(new WallpapersTab(wallpapers, closeDialog, theme)),
            new Tab(new ConfigurationTab(wallpapers, theme)),
        ];

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
        if (input.Key == DialogKey.Escape)
        {
            return DialogInputResult.Close;
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
        Style = ModulesCommon.PopupStyle(_theme) with { Padding = 24, Spacing = 8 },
        Children = [BuildTabs(), ActiveTab.Draw()],
    };

    private BoxNode BuildTabs() => new(height: 46)
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
        tab.BoxState.Background = Color.LerpSmooth(tab.BoxState.Background, target, 18.0f, Renderer.DeltaTime);

        return new BoxNode(tab.InternalTab is ConfigurationTab ? 46 : null)
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
            Children = tab.InternalTab is ConfigurationTab
                ? [new ImageNode(tab.Icon, 18, 18, _theme.Text)]
                : [new ImageNode(tab.Icon, 18, 18, _theme.Text), new TextNode(tab.Title, 15, _theme.Text)],
        };
    }

    private void SelectTab(int index)
    {
        _activeTabIndex = index;
        ActiveTab.Activate();
    }

    public void Dispose()
    {
        foreach (var tab in _tabs)
        {
            if (tab.InternalTab is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Dialogs;

internal sealed class SettingsDialog : IDialogWindow, IDisposable
{

    private sealed class Tab(IMainDialogTab content)
    {
        internal IMainDialogTab Content { get; } = content;
        internal ModulesCommon.BoxState State { get; } = new();
    }

    private readonly Tab[] _tabs;
    private readonly Theme _theme;
    private int _activeTabIndex;

    private IMainDialogTab ActiveTab => _tabs[_activeTabIndex].Content;

    internal SettingsDialog(
        StatusBarServices services,
        CompositeWindowConfiguration configuration,
        TabsService tabs,
        Action<IReadOnlyList<IMainDialogTab>> openCompositeWindow,
        Theme theme)
    {
        _theme = theme;
        _tabs =
        [
            new Tab(new ConfigurationTab(services.Wallpapers, services.History, theme)),
            new Tab(new CompositeWindowsConfigurationTab(configuration, tabs, openCompositeWindow, theme)),
        ];
    }

    public void OnOpened()
    {
        ActiveTab.Activate();
    }

    public void OnClosed()
    {
    }

    public DialogInputResult HandleInput(DialogInput input)
    {
        if (input.Key == DialogKey.Escape)
        {
            return ActiveTab.HandleEscape()
                ? DialogInputResult.None
                : DialogInputResult.Close;
        }

        switch (input.Key)
        {
            case DialogKey.Backspace:
                ActiveTab.HandleBackspace();
                break;
            case DialogKey.Enter:
                ActiveTab.ActivateSelection();
                break;
            case DialogKey.Tab:
                SelectTab((_activeTabIndex + 1) % _tabs.Length);
                break;
            case DialogKey.Up:
                ActiveTab.MoveSelection(SelectionDirection.Up);
                break;
            case DialogKey.Left:
                ActiveTab.MoveSelection(SelectionDirection.Left);
                break;
            case DialogKey.Right:
                ActiveTab.MoveSelection(SelectionDirection.Right);
                break;
            case DialogKey.Down:
                ActiveTab.MoveSelection(SelectionDirection.Down);
                break;
            default:
                if (!string.IsNullOrEmpty(input.Text))
                {
                    ActiveTab.HandleTextInput(input.Text);
                }
                break;
        }

        if (input.ScrollDelta != 0)
        {
            ActiveTab.MoveSelection(input.ScrollDelta > 0 ? SelectionDirection.Down : SelectionDirection.Up);
        }

        return DialogInputResult.None;
    }

    public Node Draw() => new BoxNode(1000)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.PopupStyle(_theme) with { Padding = 24, Spacing = 12 },
        Children =
        [
            new TextNode("Settings", 24, _theme.Text),
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
        var selected = index == _activeTabIndex;
        var normal = selected ? _theme.Active : _theme.Panel;
        var target = tab.State.Hovered ? Color.Lighten(normal, 0.12f) : normal;
        tab.State.Background = Color.LerpSmooth(tab.State.Background, target, 18, Renderer.DeltaTime);

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => SelectTab(index),
            IsHovered = tab.State.Hovered,
            Style = ModulesCommon.ModuleStyle(_theme, tab.State.Background) with
            {
                Spacing = 8,
                BorderRadius = 8,
                BorderWidth = selected ? _theme.BorderWidth : 0,
            },
            Children =
            [
                new ImageNode(tab.Content.Icon, 18, 18, _theme.Text),
                new TextNode(tab.Content.Title, 15, _theme.Text),
            ],
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
            if (tab.Content is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

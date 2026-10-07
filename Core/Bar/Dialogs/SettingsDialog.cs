using HyprNetShell.GUI;
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

    private readonly TextInputCoordinator _textInputs;
    private int _activeTabIndex;

    private IMainDialogTab ActiveTab => _tabs[_activeTabIndex].Content;

    internal SettingsDialog(
        StatusBarServices services,
        CompositeWindowConfiguration configuration,
        TabsService tabs,
        Action<IReadOnlyList<IMainDialogTab>> openCompositeWindow)
    {
        _textInputs = new TextInputCoordinator(services.ClipboardHistory);
        _tabs =
        [
            new Tab(new ConfigurationTab(services.Wallpapers, services.History, _textInputs)),
            ..(services.OnlineAccounts.HasConfiguredProviders
                ? new[] { new Tab(new OnlineAccountsConfigurationTab(services.OnlineAccounts)) }
                : []),
            new Tab(new CalendarSourcesConfigurationTab(services.Calendar, _textInputs)),
            new Tab(new CompositeWindowsConfigurationTab(
                configuration,
                tabs,
                openCompositeWindow,
                _textInputs)),
            new Tab(new InfoConfigurationTab(services)),
        ];
    }

    public void OnOpened()
    {
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

        switch (input.Key)
        {
            case DialogKey.Backspace:
                if (!_textInputs.HandleBackspace(input.ControlPressed))
                {
                    ActiveTab.HandleBackspace();
                }
                break;
            case DialogKey.Enter:
                if (!_textInputs.HandleEnter())
                {
                    ActiveTab.ActivateSelection();
                }
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
                if (!string.IsNullOrEmpty(input.Text) && !_textInputs.HandleTextInput(input.Text))
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

    public Node Draw() => new BoxNode(1000) {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.PopupStyle() with { Padding = 24, Spacing = 12 },
        Children =
        [
            new TextNode("Settings", 24),
            BuildTabs(),
            ActiveTab.Draw(),
        ],
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
        var selected = index == _activeTabIndex;
        var normal = selected ? ThemeManager.Current.Active : ThemeManager.Current.Panel;
        var target = tab.State.Hovered ? Color.Lighten(normal, 0.12f) : normal;
        tab.State.Background = Color.LerpSmooth(tab.State.Background, target, 18, Renderer.DeltaTime);

        var iconOnly = tab.Content is InfoConfigurationTab;
        return new BoxNode(width: iconOnly ? 46 : null, height: 46) {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => SelectTab(index),
            IsHovered = tab.State.Hovered,
            Style = ModulesCommon.ModuleStyle(tab.State.Background) with {
                Spacing = 8,
                BorderRadius = 8,
                BorderWidth = selected ? ThemeManager.Current.Border.Width : 0,
            },
            Children = iconOnly
                ? [new ImageNode(tab.Content.Icon, color: ThemeManager.Current.Text)]
                : [new ImageNode(tab.Content.Icon, color: ThemeManager.Current.Text), tab.Content.Title],
        };
    }

    private void SelectTab(int index)
    {
        _textInputs.Deactivate();
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

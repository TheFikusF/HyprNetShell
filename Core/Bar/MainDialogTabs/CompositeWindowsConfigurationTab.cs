using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class CompositeWindowsConfigurationTab(
    CompositeWindowConfiguration configuration,
    TabsService tabs,
    Action<IReadOnlyList<IMainDialogTab>> openWindow,
    Theme theme) : IMainDialogTab
{
    private enum EditedField
    {
        None,
        Name,
        Hotkey,
    }

    private readonly Dictionary<string, ModulesCommon.BoxState> _buttonStates = [];
    private int _selectedIndex;
    private bool _isNew;
    private string _id = "";
    private string _name = "";
    private string _hotkey = "";
    private HashSet<string> _tabs = new(StringComparer.Ordinal);
    private EditedField _editedField;
    private string _message = "";

    public string Id => "composite-windows";
    public string Title => "Composite windows";
    public SvgAsset Icon => Icons.CompositeWindow;

    public void Activate()
    {
        if (!_isNew && configuration.Windows.Count > 0)
        {
            Select(Math.Clamp(_selectedIndex, 0, configuration.Windows.Count - 1));
        }
        else if (configuration.Windows.Count == 0 && !_isNew)
        {
            StartNew();
        }
    }

    public void HandleTextInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        switch (_editedField)
        {
            case EditedField.Name when _name.Length < 48:
                _name += text;
                break;
            case EditedField.Hotkey when _hotkey.Length < 80:
                _hotkey += text.ToUpperInvariant();
                break;
        }
    }

    public void HandleBackspace()
    {
        switch (_editedField)
        {
            case EditedField.Name:
                _name = MainDialogTabUi.RemoveLastTextElement(_name);
                break;
            case EditedField.Hotkey:
                _hotkey = MainDialogTabUi.RemoveLastTextElement(_hotkey);
                break;
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (_editedField != EditedField.None || configuration.Windows.Count == 0)
        {
            return;
        }

        if (direction == SelectionDirection.Up)
        {
            Select(Math.Max(0, _selectedIndex - 1));
        }
        else if (direction == SelectionDirection.Down)
        {
            Select(Math.Min(configuration.Windows.Count - 1, _selectedIndex + 1));
        }
    }

    public void ActivateSelection()
    {
    }

    public Node Draw() => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 12 },
        Children =
        [
            BuildWindowList(),
            new BoxNode
            {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Stretch,
                Style = new Style { Spacing = 10 },
                Children =
                [
                    MainDialogTabUi.BuildSectionHeader(
                        _isNew ? "New composite window" : "Edit composite window",
                        "Choose its tabs and an optional Hyprland hotkey"),
                    BuildInput("Name", _name, "Window name", EditedField.Name),
                    BuildInput("Hotkey", _hotkey, "Example: SUPER + SPACE", EditedField.Hotkey),
                    new TextNode("Tabs", 15, theme.Text),
                    new BoxNode
                    {
                        Style = new Style { Spacing = 8 },
                        Children = [..tabs.Tabs.Select(BuildTabToggle)],
                    },
                    new TextNode(
                        _message.Length == 0 ? "Changes are applied after Save." : _message,
                        theme.TextSize,
                        _message.StartsWith("Saved", StringComparison.Ordinal) ? theme.Active : theme.Muted),
                    BuildActions(),
                ],
            },
        ],
    };

    private Node BuildWindowList()
    {
        var rows = configuration.Windows.Select((window, index) => BuildWindowRow(window, index)).ToList();
        rows.Add(BuildButton("New window", Icons.Add, "new", StartNew));
        return new BoxNode(245)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children = rows,
        };
    }

    private Node BuildWindowRow(CompositeWindowDefinition window, int index)
    {
        var state = State("window-" + window.Id);
        var selected = !_isNew && index == _selectedIndex;
        var normal = selected ? theme.Active : theme.Panel;
        var target = state.Hovered ? Color.Lighten(normal, 0.12f) : normal;
        state.Background = Color.LerpSmooth(state.Background, target, 18, Renderer.DeltaTime);

        return new BoxNode
        {
            Direction = Direction.Vertical,
            IsHovered = state.Hovered,
            OnClick = () => Select(index),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 12,
                BorderRadius = 8,
                BorderWidth = selected ? theme.BorderWidth : 0,
                Spacing = 3,
            },
            Children =
            [
                new TextNode(window.Name, 15, theme.Text),
                new TextNode(window.Hotkey.Length == 0 ? "No hotkey" : window.Hotkey, 12, theme.Muted),
            ],
        };
    }

    private Node BuildInput(string label, string value, string placeholder, EditedField field)
    {
        var active = _editedField == field;
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            OnClick = () => _editedField = field,
            Style = ModulesCommon.ModuleStyle(theme, active ? theme.Active : theme.Panel) with
            {
                Padding = 12,
                BorderRadius = 8,
                BorderWidth = active ? theme.BorderWidth : 0,
                Spacing = 5,
            },
            Children =
            [
                new TextNode(label, 12, theme.Muted),
                new TextNode(value.Length == 0 ? placeholder : value, 15, value.Length == 0 ? theme.Muted : theme.Text),
            ],
        };
    }

    private Node BuildTabToggle(IMainDialogTab tab)
    {
        var selected = _tabs.Contains(tab.Id);
        return BuildButton(
            tab.Title,
            tab.Icon,
            "tab-" + tab.Id,
            () =>
            {
                if (!_tabs.Add(tab.Id))
                {
                    _tabs.Remove(tab.Id);
                }
            },
            selected);
    }

    private Node BuildActions() => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.End,
        Style = new Style { Spacing = 8 },
        Children =
        [
            ..(!_isNew
                ? new[]
                {
                    BuildButton("Launch", Icons.Play, "launch", () => openWindow(tabs.Resolve(_tabs))),
                    BuildButton("Delete", Icons.Delete, "delete", Delete),
                }
                : []),
            BuildButton("Save", Icons.Save, "save", Save, true),
        ],
    };

    private Node BuildButton(
        string label,
        SvgAsset icon,
        string key,
        Action action,
        bool selected = false)
    {
        var state = State(key);
        var normal = selected ? theme.Active : theme.Panel;
        var target = state.Hovered ? Color.Lighten(normal, 0.12f) : normal;
        state.Background = Color.LerpSmooth(state.Background, target, 18, Renderer.DeltaTime);
        return new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = action,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = new Insets(12, 9),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 7,
            },
            Children = [new ImageNode(icon, 16, 16, theme.Text), new TextNode(label, 14, theme.Text)],
        };
    }

    private void StartNew()
    {
        _isNew = true;
        _id = Guid.NewGuid().ToString("N");
        _name = $"Composite {configuration.Windows.Count + 1}";
        _hotkey = "";
        _tabs = tabs.Tabs.Count > 0 ? [tabs.Tabs[0].Id] : [];
        _editedField = EditedField.Name;
        _message = "";
    }

    private void Select(int index)
    {
        if (configuration.Windows.Count == 0)
        {
            StartNew();
            return;
        }

        _selectedIndex = Math.Clamp(index, 0, configuration.Windows.Count - 1);
        var window = configuration.Windows[_selectedIndex];
        _isNew = false;
        _id = window.Id;
        _name = window.Name;
        _hotkey = window.Hotkey;
        _tabs = [..window.TabIds];
        _editedField = EditedField.None;
        _message = "";
    }

    private void Save()
    {
        if (!configuration.TryUpsert(
                new CompositeWindowDefinition(_id, _name, _hotkey, [.._tabs]),
                out var error))
        {
            _message = error;
            return;
        }

        _selectedIndex = configuration.Windows.ToList().FindIndex(window => window.Id == _id);
        _isNew = false;
        _editedField = EditedField.None;
        _message = "Saved. Hotkey bindings were refreshed.";
    }

    private void Delete()
    {
        configuration.Delete(_id);
        if (configuration.Windows.Count == 0)
        {
            StartNew();
        }
        else
        {
            Select(Math.Min(_selectedIndex, configuration.Windows.Count - 1));
        }
    }

    private ModulesCommon.BoxState State(string key)
    {
        if (!_buttonStates.TryGetValue(key, out var state))
        {
            state = new ModulesCommon.BoxState();
            _buttonStates.Add(key, state);
        }

        return state;
    }

}

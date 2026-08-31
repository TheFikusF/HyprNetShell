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
    private List<string> _tabs = [];
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

    public bool HandleEscape()
    {
        if (_editedField != EditedField.None)
        {
            _editedField = EditedField.None;
            return true;
        }

        return false;
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
                Style = new Style { Spacing = 12 },
                Children =
                [
                    MainDialogTabUi.BuildSectionHeader(
                        _isNew ? "New composite window" : "Edit composite window",
                        "Choose its tabs and an optional Hyprland hotkey"),
                    BuildInput("Name", _name, "Window name", EditedField.Name),
                    BuildInput("Hotkey", _hotkey, "Example: SUPER + SPACE", EditedField.Hotkey),
                    new TextNode("Tabs", theme.TextSize, theme.Text),
                    BuildTabGrid(),
                    new TextNode(
                        _message.Length == 0 ? "Changes are applied after Save." : _message,
                        theme.TextSize,
                        _message.StartsWith("Saved", StringComparison.Ordinal) ? theme.Active : theme.Muted),
                    BuildActions(),
                ],
            },
        ],
    };

    private BoxNode BuildWindowList()
    {
        return new BoxNode(245)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = Style.Spacer,
            Children = [.. configuration.Windows.Select(BuildWindowRow),
                BuildButton("New window", Icons.Add, "new", StartNew)],
        };
    }

    private BoxNode BuildWindowRow(CompositeWindowDefinition window, int index)
    {
        var selected = !_isNew && index == _selectedIndex;
        var state = _buttonStates.GetState("window-" + window.Id, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
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
                new TextNode(window.Name, theme.TextSize, theme.Text),
                new TextNode(
                    window.Hotkey.Length == 0 ? "No hotkey" : window.Hotkey,
                    12,
                    selected ? theme.Text : theme.Muted),
            ],
        };
    }

    private BoxNode BuildInput(string label, string value, string placeholder, EditedField field)
    {
        var active = _editedField == field;
        var caret = active && Math.Sin(Environment.TickCount64 / 200.0) > 0 ? "|" : "";
        var displayedValue = active
            ? (value.Length == 0 ? placeholder : value) + caret
            : value.Length == 0 ? placeholder : value;
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
                new TextNode(label, 12, active ? theme.Text : theme.Muted),
                new TextNode(displayedValue, theme.TextSize, active || value.Length > 0 ? theme.Text : theme.Muted),
            ],
        };
    }

    private BoxNode BuildTabGrid()
    {
        var orderedTabs = OrderedTabs().ToArray();
        var columnLength = (orderedTabs.Length + 1) / 2;
        return new BoxNode(Style.Spacer, ItemsAlignment.Stretch, ItemsAlignment.Start)
        {
            Children =
            [
                ..orderedTabs
                    .Chunk(columnLength)
                    .Select(column => new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = Style.Spacer,
                        Children = [..column.Select(BuildTabToggle)],
                    }),
            ],
        };
    }

    private IEnumerable<IMainDialogTab> OrderedTabs()
    {
        var byId = tabs.Tabs.ToDictionary(tab => tab.Id, StringComparer.Ordinal);
        foreach (var id in _tabs)
        {
            if (byId.Remove(id, out var tab))
            {
                yield return tab;
            }
        }

        foreach (var tab in tabs.Tabs.Where(tab => byId.ContainsKey(tab.Id)))
        {
            yield return tab;
        }
    }

    private Node BuildTabToggle(IMainDialogTab tab)
    {
        var selectedIndex = _tabs.IndexOf(tab.Id);
        var selected = selectedIndex >= 0;
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            Style = Style.Spacer,
            Children =
            [
                BuildButton(
                    selected ? $"{selectedIndex + 1}. {tab.Title}" : tab.Title,
                    tab.Icon,
                    "tab-" + tab.Id,
                    () => ToggleTab(tab.Id),
                    selected),
                ..BuildMoveButtons(tab.Id, selectedIndex),
            ],
        };
    }

    private IEnumerable<Node> BuildMoveButtons(string tabId, int selectedIndex)
    {
        if (selectedIndex < 0)
        {
            yield break;
        }

        yield return BuildMoveButton(tabId, -1, selectedIndex > 0, Icons.ChevronUp);
        yield return BuildMoveButton(tabId, 1, selectedIndex < _tabs.Count - 1, Icons.ChevronDown);
    }

    private BoxNode BuildMoveButton(string tabId, int offset, bool enabled, SvgAsset icon)
    {
        var state = _buttonStates
            .GetState($"move-{tabId}-{offset}", theme.Panel)
            .UpdateColor(theme.Panel);
        if (!enabled)
        {
            state.Hovered.Value = false;
        }

        return new BoxNode(32, 32)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = enabled ? state.Hovered : null,
            OnClick = enabled ? () => MoveTab(tabId, offset) : null,
            Opacity = enabled ? 1.0f : 0.35f,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 8,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new ImageNode(icon, 16, 16, theme.Text)],
        };
    }

    private void ToggleTab(string tabId)
    {
        var index = _tabs.IndexOf(tabId);
        if (index >= 0)
        {
            _tabs.RemoveAt(index);
        }
        else
        {
            _tabs.Add(tabId);
        }
    }

    private void MoveTab(string tabId, int offset)
    {
        var index = _tabs.IndexOf(tabId);
        var target = index + offset;
        if (index < 0 || target < 0 || target >= _tabs.Count)
        {
            return;
        }

        (_tabs[index], _tabs[target]) = (_tabs[target], _tabs[index]);
    }

    private BoxNode BuildActions() => new()
    {
        HorizontalAlignment = ItemsAlignment.End,
        Style = Style.Spacer,
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

    private BoxNode BuildButton(
        string label,
        SvgAsset icon,
        string key,
        Action action,
        bool selected = false)
    {
        var state = _buttonStates.GetState(key, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
        return new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = action,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = new Insets(12, 8),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children = [new ImageNode(icon, 16, 16, theme.Text), new TextNode(label, theme.TextSize, theme.Text)],
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
        _tabs = [.. window.TabIds];
        _editedField = EditedField.None;
        _message = "";
    }

    private void Save()
    {
        if (!configuration.TryUpsert(
                new CompositeWindowDefinition(_id, _name, _hotkey, [.. _tabs]),
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
}

using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

using HyprNetShell.Core.Bar.Common;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ClipboardManagerTab(ClipboardHistoryService history, Action closeDialog, Theme theme)
    : IMainDialogTab
{
    private sealed class ActionButtonState : ModulesCommon.BoxState
    {
        public float IconOpacity { get; set; }
    }

    private sealed class ClipboardButtonState : ModulesCommon.BoxState
    {
        public ActionButtonState Pin { get; } = new();
        public ActionButtonState Delete { get; } = new();
    }

    private const int FUZZY_SCORE_CUTOFF = 35;
    private const int PREVIEW_MAX_WIDTH = 700;
    private readonly Dictionary<string, ClipboardButtonState> _buttonsState = new();
    private IReadOnlyList<ClipboardHistoryEntry> _entries = [];
    private IReadOnlyList<ClipboardHistoryEntry> _filteredEntries = [];
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;
    private int _loadedVersion = -1;
    private HistoryDateRange _dateRange;
    private DropdownNode? _dateDropdown;

    public string Id => "clipboard";
    public string Title => "Clipboard";
    public SvgAsset Icon => Icons.Clipboard;

    public void Activate() => RefreshEntries();

    public void HandleTextInput(string text)
    {
        _query += text;
        ApplyFilter();
    }

    public void HandleBackspace()
    {
        if (_query.Length > 0)
        {
            _query = MainDialogTabUi.RemoveLastTextElement(_query);
            ApplyFilter();
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            BoundedListUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1,
                _filteredEntries.Count);
        }
    }

    public void ActivateSelection()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _filteredEntries.Count)
        {
            return;
        }

        _ = history.CopyAsync(_filteredEntries[_selectedIndex]);
        closeDialog();
    }

    public Node Draw()
    {
        if (_loadedVersion != history.Version)
        {
            RefreshEntries();
        }

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Clipboard",
                    MainDialogTabUi.ResultCount(
                        _selectedIndex,
                        _filteredEntries.Count,
                        "Clipboard history is empty")),
                new BoxNode(height: 46)
                {
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 8 },
                    Children =
                    [
                        MainDialogTabUi.BuildInput(_query, "Search clipboard history..."),
                        BuildDateDropdown(),
                    ],
                },
                BoundedListUi.BuildScrollableResults(
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = new Style { Spacing = 8 },
                        Children =
                        [
                            .._filteredEntries
                                .VisibleItems(_firstIndex)
                                .Select(item => BuildRow(item.Item, item.Index)),
                        ],
                    },
                    _firstIndex,
                    _filteredEntries.Count,
                    BoundedListUi.DefaultVisibleItemCount,
                    theme),
            ],
        };
    }

    private BoxNode BuildRow(ClipboardHistoryEntry entry, int index)
    {
        var selected = index == _selectedIndex;
        var stateKey = $"{entry.MimeType}\0{entry.Hash}";
        var state = _buttonsState.GetState(stateKey, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                if (state.Pin.Hovered.Value || state.Delete.Hovered.Value)
                {
                    return;
                }

                _selectedIndex = index;
                ActivateSelection();
            },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.Border.Width : 0,
                Padding = new Insets(16, 8),
                Spacing = 14,
            },
            Children =
            [
                new BoxNode
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 14 },
                    Children =
                    [
                        entry.Image is not null
                            ? new ImageNode(entry.Image, 46, 46)
                            : new ImageNode(Icons.Copy, 30, 30, theme.Text),
                        new TextNode(entry.Preview, theme.Text, theme.Text,
                            maxWidth: PREVIEW_MAX_WIDTH,
                            wrapping: TextWrapping.Wrap,
                            maxLines: 5),
                    ],
                },
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    BuildPinButton(entry, selected || state.Hovered.Value, state.Pin),
                    BuildDeleteButton(entry, selected || state.Hovered.Value, state.Delete),
                },
            ],
        };
    }

    private BoxNode BuildPinButton(
        ClipboardHistoryEntry entry,
        bool active,
        ActionButtonState state)
    {
        var transparent = Color.White with { A = 0.0f };
        var hover = Color.White with { A = 0.3f };
        state.Background = Color.LerpSmooth(
            state.Background,
            state.Hovered.Value ? hover : transparent,
            18.0f,
            Renderer.DeltaTime);
        state.IconOpacity = PrimitivesMath.LerpSmooth(
            state.IconOpacity,
            entry.IsPinned || active ? 1.0f : 0.0f,
            18.0f,
            Renderer.DeltaTime);

        return new BoxNode(32, 32)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => history.TogglePinned(entry),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
                ShadowColor = null,
            },
            Children =
            [
                new ImageNode(entry.IsPinned && active ? Icons.PinOff : Icons.Pin, 18, 18, theme.Text)
                {
                    Opacity = state.IconOpacity,
                },
            ],
        };
    }

    private BoxNode BuildDeleteButton(
        ClipboardHistoryEntry entry,
        bool active,
        ActionButtonState state)
    {
        var transparent = Color.White with { A = 0.0f };
        var hover = Color.White with { A = 0.3f };
        state.Background = Color.LerpSmooth(
            state.Background,
            state.Hovered.Value ? hover : transparent,
            18.0f,
            Renderer.DeltaTime);
        state.IconOpacity = PrimitivesMath.LerpSmooth(
            state.IconOpacity,
            active ? 1.0f : 0.0f,
            18.0f,
            Renderer.DeltaTime);

        return new BoxNode(32, 32)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => history.Delete(entry),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
                ShadowColor = null,
            },
            Children =
            [
                new ImageNode(Icons.Delete, 18, 18, theme.Text)
                {
                    Opacity = state.IconOpacity,
                },
            ],
        };
    }

    private DropdownNode BuildDateDropdown()
    {
        _dateDropdown ??= new DropdownNode(
            160,
            HistoryDateFilter.Labels,
            (int)_dateRange,
            Icons.ChevronDown,
            Icons.Check,
            selected =>
            {
                _dateRange = (HistoryDateRange)selected;
                ApplyFilter();
            })
        {
            FontSize = theme.Text,
            BackgroundColor = theme.Panel,
            HoverColor = Color.Lighten(theme.Panel, 0.18f),
            SelectedColor = theme.Active,
            BorderColor = theme.Border,
            BorderWidth = theme.Border.Width,
            BorderRadius = 8,
            TextColor = theme.Text,
        };
        _dateDropdown.SelectedIndex = (int)_dateRange;
        return _dateDropdown;
    }

    private void RefreshEntries()
    {
        int version;
        do
        {
            version = history.Version;
            _entries = history.Snapshot();
        } while (version != history.Version);

        _loadedVersion = version;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var dateFilteredEntries = _entries
            .Where(entry => HistoryDateFilter.Includes(_dateRange, entry.CapturedAt));
        _filteredEntries = string.IsNullOrWhiteSpace(_query)
            ? dateFilteredEntries.ToArray()
            : dateFilteredEntries
                .Select(entry => (Entry: entry, Score: FuzzySearch.Score(_query, entry.Preview)))
                .Where(result => result.Score >= FUZZY_SCORE_CUTOFF)
                .OrderByDescending(result => result.Entry.IsPinned)
                .ThenByDescending(result => result.Score)
                .Select(result => result.Entry)
                .ToArray();
        _firstIndex = 0;
        _selectedIndex = 0;
    }
}

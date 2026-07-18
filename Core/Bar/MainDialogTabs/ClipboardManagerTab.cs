using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using FuzzySharp;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ClipboardManagerTab(ClipboardHistoryService history, Action closeDialog, Theme theme)
    : IMainDialogTab
{
    private const int FUZZY_SCORE_CUTOFF = 35;
    private readonly Dictionary<int, ModulesCommon.BoxState> _buttonsState = new();
    private IReadOnlyList<ClipboardHistoryEntry> _entries = [];
    private IReadOnlyList<ClipboardHistoryEntry> _filteredEntries = [];
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;
    private int _loadedVersion = -1;

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
            MainDialogTabUi.MoveSelection(
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
                MainDialogTabUi.BuildInput(_query, "Search clipboard history..."),
                MainDialogTabUi.BuildScrollableResults(
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = new Style { Spacing = 8 },
                        Children =
                        [
                            .._filteredEntries
                                .Skip(_firstIndex)
                                .Take(MainDialogTabUi.VISIBLE_ROW_COUNT)
                                .Select((entry, visibleIndex) => BuildRow(entry, _firstIndex + visibleIndex)),
                        ],
                    },
                    _firstIndex,
                    _filteredEntries.Count,
                    MainDialogTabUi.VISIBLE_ROW_COUNT),
            ],
        };
    }

    private BoxNode BuildRow(ClipboardHistoryEntry entry, int index)
    {
        var selected = index == _selectedIndex;
        var state = _buttonsState.GetState(index, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
        return new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                _selectedIndex = index;
                ActivateSelection();
            },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.BorderWidth : 0,
                Padding = new Insets(16, 8),
                Spacing = 14,
            },
            Children =
            [
                entry.Image is not null
                    ? new ImageNode(entry.Image, 46, 46)
                    : new ImageNode(Icons.Copy, 30, 30, theme.Text),
                new TextNode(entry.Preview, 15, theme.Text, wrapping: TextWrapping.Wrap, maxLines: 5),
            ],
        };
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
        _filteredEntries = string.IsNullOrWhiteSpace(_query)
            ? _entries
            : _entries
                .Select(entry => (Entry: entry, Score: Fuzz.WeightedRatio(_query, entry.Preview)))
                .Where(result => result.Score >= FUZZY_SCORE_CUTOFF)
                .OrderByDescending(result => result.Score)
                .Select(result => result.Entry)
                .ToArray();
        _firstIndex = 0;
        _selectedIndex = 0;
    }
}
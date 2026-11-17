using System.Text;
using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.KdeConnect;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Core.Bar.Common;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ClipboardManagerTab(
    ClipboardHistoryService history,
    KdeConnectService kdeConnect,
    Action closeDialog,
    TextInputCoordinator inputs) : IMainDialogTab
{
    private sealed class ClipboardButtonState : ModulesCommon.BoxState
    {
        public AnimatedIconButton Send { get; } = new();
        public AnimatedIconButton Pin { get; } = new();
        public AnimatedIconButton Delete { get; } = new();
    }

    private const int FUZZY_SCORE_CUTOFF = 35;
    private readonly Dictionary<string, ClipboardButtonState> _buttonsState = new();
    private readonly TextInputCoordinator.Input _queryInput = inputs.Create(
        "", "", "Search clipboard history...", 4096, alwaysActive: true);
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

    public void Activate()
    {
        inputs.Configure(_queryInput, query =>
        {
            _query = query;
            ApplyFilter();
        });
        inputs.Activate(_queryInput);
        RefreshEntries();
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            BoundedListUi.MoveSelection(ref _selectedIndex, ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1, _filteredEntries.Count);
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

        return new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader("Clipboard",
                    MainDialogTabUi.ResultCount(_selectedIndex, _filteredEntries.Count, "Clipboard history is empty")),
                new BoxNode(height: 46)
                {
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 8 },
                    Children = [inputs.Build(_queryInput), BuildDateDropdown()],
                },
                BoundedListUi.BuildScrollableResults(
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = new Style { Spacing = 8 },
                        Children = [.._filteredEntries.VisibleItems(_firstIndex).Select(item => BuildRow(item.Item, item.Index))],
                    },
                    _firstIndex, _filteredEntries.Count, BoundedListUi.DEFAULT_VISIBLE_ITEM_COUNT),
            ],
        };
    }

    private BoxNode BuildRow(ClipboardHistoryEntry entry, int index)
    {
        var selected = index == _selectedIndex;
        var stateKey = $"{entry.MimeType}\0{entry.Hash}";
        var state = _buttonsState.GetState(stateKey, ThemeManager.Current.Panel)
            .UpdateColor(selected ? ThemeManager.Current.Active : ThemeManager.Current.Panel);
        var active = selected || state.Hovered.Value;
        var sendAvailable = entry.Image is null && kdeConnect.Snapshot.Devices.Any(
            static device => device.IsPaired && device.IsReachable);
        return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                if (state.Send.Hovered.Value || state.Pin.Hovered.Value || state.Delete.Hovered.Value)
                {
                    return;
                }

                _selectedIndex = index;
                ActivateSelection();
            },
            IsHoveredThrough = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                BorderRadius = 8,
                BorderWidth = selected ? ThemeManager.Current.Border.Width : 0,
                Padding = new Insets(16, 8),
                Spacing = 14,
            },
            Children =
            [
                entry.Image is not null
                    ? new ImageNode(entry.Image, 46, 46)
                    : new ImageNode(Icons.Copy, 30, 30, ThemeManager.Current.Text),
                new BoxNode
                {
                    Flex = 1,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Center,
                    Children = [new TextNode(entry.Preview, wrapping: TextWrapping.Wrap, maxLines: 5)],
                },
                new BoxNode(height: 32)
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    HorizontalAlignment = ItemsAlignment.End,
                    Style = new Style(),
                    Flex = 0,
                    Children =
                    [
                        state.Send.Build(Icons.Smartphone, active && sendAvailable,
                            () => _ = kdeConnect.SendClipboardTextAsync(Encoding.UTF8.GetString(entry.Data))),
                        state.Pin.Build(entry.IsPinned && active ? Icons.PinOff : Icons.Pin,
                            active || entry.IsPinned, () => history.TogglePinned(entry)),
                        state.Delete.Build(Icons.Delete, active, () => history.Delete(entry)),
                    ],
                },
            ],
        };
    }

    private DropdownNode BuildDateDropdown()
    {
        _dateDropdown ??= new DropdownNode(160, HistoryDateFilter.Labels, (int)_dateRange,
            Icons.ChevronDown, Icons.Check, selected =>
            {
                _dateRange = (HistoryDateRange)selected;
                ApplyFilter();
            }) {
            FontSize = ThemeManager.Current.Text,
            BackgroundColor = ThemeManager.Current.Panel,
            HoverColor = Color.Lighten(ThemeManager.Current.Panel, 0.18f),
            SelectedColor = ThemeManager.Current.Active,
            BorderWidth = ThemeManager.Current.Border.Width,
            BorderRadius = 8,
            TextColor = ThemeManager.Current.Text,
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
        var dateFilteredEntries = _entries.Where(entry => HistoryDateFilter.Includes(_dateRange, entry.CapturedAt));
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

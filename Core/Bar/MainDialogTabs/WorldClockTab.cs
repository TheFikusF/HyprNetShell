using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class WorldClockTab : IMainDialogTab
{
    private readonly WorldClockService _clocks;
    private readonly Theme _theme;
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];
    private IReadOnlyList<WorldClock> _filteredClocks = [];
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;

    public WorldClockTab(Theme theme)
        : this(WorldClockService.Shared, theme)
    {
    }

    public WorldClockTab(WorldClockService clocks, Theme theme)
    {
        _clocks = clocks;
        _theme = theme;
        ApplyFilter();
    }

    public string Id => "world-clocks";
    public string Title => "World clocks";
    public SvgAsset Icon => Icons.Clock;

    public void Activate()
    {
        _query = "";
        _firstIndex = 0;
        _selectedIndex = 0;
        ApplyFilter();
    }

    public void HandleTextInput(string text)
    {
        _query += text;
        ApplyFilter();
    }

    public void HandleBackspace()
    {
        if (_query.Length == 0)
        {
            return;
        }

        _query = MainDialogTabUi.RemoveLastTextElement(_query);
        ApplyFilter();
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is not (SelectionDirection.Up or SelectionDirection.Down))
        {
            return;
        }

        BoundedListUi.MoveSelection(
            ref _selectedIndex,
            ref _firstIndex,
            direction == SelectionDirection.Up ? -1 : 1,
            _filteredClocks.Count);
    }

    public void ActivateSelection()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _filteredClocks.Count)
        {
            return;
        }

        _clocks.Toggle(_filteredClocks[_selectedIndex].TimeZoneId);
    }

    public Node Draw()
    {
        var selectedCount = _clocks.SelectedClocks.Count;
        return new BoxNode(new Style { Spacing = 8 })
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "World clocks",
                    $"{selectedCount} selected · Enter or click to toggle"),
                MainDialogTabUi.BuildInput(_query, "Search cities or time zones..."),
                BoundedListUi.BuildScrollableResults(
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = new Style { Spacing = 8 },
                        Children = _filteredClocks
                            .VisibleItems(_firstIndex)
                            .Select(item => BuildRow(item.Item, item.Index))
                            .ToArray(),
                    },
                    _firstIndex,
                    _filteredClocks.Count,
                    BoundedListUi.DefaultVisibleItemCount,
                    _theme),
            ],
        };
    }

    private BoxNode BuildRow(WorldClock clock, int index)
    {
        var selected = index == _selectedIndex;
        var enabled = _clocks.IsSelected(clock.TimeZoneId);
        var state = _rowStates
            .GetState(clock.TimeZoneId, _theme.Panel)
            .UpdateColor(selected ? _theme.Active : _theme.Panel);
        var time = WorldClockService.GetTime(clock, DateTime.UtcNow);

        return new BoxNode(height: 56)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                _selectedIndex = index;
                _clocks.Toggle(clock.TimeZoneId);
            },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
            {
                Padding = new Insets(14, 8),
                BorderRadius = 8,
                BorderWidth = selected ? _theme.BorderWidth : 0,
            },
            Children =
            [
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new CheckboxNode(enabled, Icons.Check)
                    {
                        SelectedColor = selected ? _theme.Text : _theme.Active,
                        UnselectedColor = selected ? _theme.Text : _theme.Muted,
                        BackgroundColor = selected ? _theme.Active : _theme.Panel,
                        CheckColor = selected ? _theme.Active : _theme.Text,
                    },
                    new TextNode(clock.DisplayName, _theme.TextSize, _theme.Text),
                    new TextNode(clock.TimeZoneId, _theme.TextSize, selected ? _theme.Text : _theme.Muted),
                },
                new TextNode(time.ToString("HH:mm"), 18, _theme.Text),
            ],
        };
    }

    private void ApplyFilter()
    {
        var terms = _query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        _filteredClocks = terms.Length == 0
            ? _clocks.AvailableClocks
            : _clocks.AvailableClocks
                .Where(clock => terms.All(term =>
                    clock.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    clock.TimeZoneId.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        _firstIndex = 0;
        _selectedIndex = _filteredClocks.Count == 0 ? -1 : 0;
    }
}

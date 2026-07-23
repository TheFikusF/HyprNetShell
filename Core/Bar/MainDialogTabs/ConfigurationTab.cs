using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ConfigurationTab(WallpaperModuleService wallpapers, Theme theme) : IMainDialogTab
{
    private readonly ModulesCommon.BoxState _toggleState = new();
    private readonly ModulesCommon.BoxState _decreaseState = new();
    private readonly ModulesCommon.BoxState _increaseState = new();
    private int _selectedIndex;

    public string Title => "Configuration";
    public SvgAsset Icon => Icons.Settings;

    public void Activate() => _selectedIndex = 0;

    public void HandleTextInput(string text)
    {
    }

    public void HandleBackspace()
    {
    }

    public void MoveSelection(SelectionDirection direction)
    {
        _selectedIndex = direction switch
        {
            SelectionDirection.Up => 0,
            SelectionDirection.Down when _selectedIndex == 0 => 1,
            SelectionDirection.Left when _selectedIndex > 0 => 1,
            SelectionDirection.Right when _selectedIndex > 0 => 2,
            _ => _selectedIndex,
        };
    }

    public void ActivateSelection()
    {
        switch (_selectedIndex)
        {
            case 0:
                wallpapers.SetSlideshowEnabled(!wallpapers.SlideshowEnabled);
                break;
            case 1:
                wallpapers.SetDurationMinutes(wallpapers.DurationMinutes - 1);
                break;
            case 2:
                wallpapers.SetDurationMinutes(wallpapers.DurationMinutes + 1);
                break;
        }
    }

    public Node Draw() => new BoxNode
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 12 },
        Children =
        [
            MainDialogTabUi.BuildSectionHeader("Wallpaper settings", "Saved automatically"),
            BuildSlideshowToggle(),
            BuildDurationControl(),
            new TextNode($"Wallpaper directory: {wallpapers.WallpaperDirectory}", theme.TextSize, theme.Muted),
        ],
    };

    private Node BuildSlideshowToggle()
    {
        var enabled = wallpapers.SlideshowEnabled;
        var state = _toggleState.UpdateColor(_selectedIndex == 0 ? theme.Active : theme.Panel);
        return new BoxNode(height: 64)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => wallpapers.SetSlideshowEnabled(!enabled),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = _selectedIndex == 0 ? theme.BorderWidth : 0,
            },
            Children =
            [
                new TextNode("Wallpaper slideshow", 16, theme.Text),
                new BoxNode(74, 28)
                {
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style
                    {
                        BackgroundColor = enabled ? theme.Active : theme.Muted,
                        BorderRadius = 8,
                    },
                    Children = [new TextNode(enabled ? "ON" : "OFF", theme.TextSize, theme.Text)],
                },
            ],
        };
    }

    private Node BuildDurationControl()
    {
        var duration = wallpapers.DurationMinutes;
        return new BoxNode(height: 78)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    Style = new Style { Spacing = 4 },
                    Children =
                    [
                        new TextNode("Slideshow duration", 16, theme.Text),
                        new TextNode("Time between wallpaper changes", theme.TextSize, theme.Muted),
                    ],
                },
                new BoxNode
                {
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 8 },
                    Children =
                    [
                        BuildDurationButton("-", -1, 1, _decreaseState),
                        new BoxNode(92, 34)
                        {
                            HorizontalAlignment = ItemsAlignment.Center,
                            VerticalAlignment = ItemsAlignment.Center,
                            Children = [new TextNode($"{duration} min", 16, theme.Text)],
                        },
                        BuildDurationButton("+", 1, 2, _increaseState),
                    ],
                },
            ],
        };
    }

    private Node BuildDurationButton(string label, int delta, int index, ModulesCommon.BoxState buttonState)
    {
        var state = buttonState.UpdateColor(_selectedIndex == index ? theme.Active : theme.Muted);
        return new BoxNode(38, 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => wallpapers.SetDurationMinutes(wallpapers.DurationMinutes + delta),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = _selectedIndex == index ? theme.BorderWidth : 0,
            },
            Children = [new TextNode(label, 20, theme.Text)],
        };
    }
}

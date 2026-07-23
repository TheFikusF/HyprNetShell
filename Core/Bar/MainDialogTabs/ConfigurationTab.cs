using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ConfigurationTab(WallpaperModuleService wallpapers, Theme theme) : IMainDialogTab
{
    private readonly RefFloat _slideshowSwitchAnimation = new(wallpapers.SlideshowEnabled ? 1.0f : 0.0f);
    private readonly ModulesCommon.BoxState _decreaseState = new();
    private readonly ModulesCommon.BoxState _increaseState = new();

    public string Title => "Configuration";
    public SvgAsset Icon => Icons.Settings;

    public void Activate()
    {
    }

    public void HandleTextInput(string text)
    {
    }

    public void HandleBackspace()
    {
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
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

    private BoxNode BuildSlideshowToggle()
    {
        var enabled = wallpapers.SlideshowEnabled;
        return new (height: 64)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => wallpapers.SetSlideshowEnabled(!enabled),
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                Padding = new Insets(18, 0),
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new TextNode("Wallpaper slideshow", 16, theme.Text),
                new SwitchNode(enabled, _slideshowSwitchAnimation)
                {
                    OffTrackColor = theme.Muted,
                    OnTrackColor = theme.Active,
                    KnobColor = theme.Text,
                },
            ],
        };
    }

    private BoxNode BuildDurationControl()
    {
        var duration = wallpapers.DurationMinutes;
        return new (height: 78)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                Padding = new Insets(18, 0),
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
                        BuildDurationButton("-", -1, _decreaseState),
                        new BoxNode(92, 34)
                        {
                            HorizontalAlignment = ItemsAlignment.Center,
                            VerticalAlignment = ItemsAlignment.Center,
                            Children = [new TextNode($"{duration} min", 16, theme.Text)],
                        },
                        BuildDurationButton("+", 1, _increaseState),
                    ],
                },
            ],
        };
    }

    private BoxNode BuildDurationButton(string label, int delta, ModulesCommon.BoxState buttonState)
    {
        var state = buttonState.UpdateColor(theme.Muted);
        return new (38, 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => wallpapers.SetDurationMinutes(wallpapers.DurationMinutes + delta),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(label, 20, theme.Text)],
        };
    }
}

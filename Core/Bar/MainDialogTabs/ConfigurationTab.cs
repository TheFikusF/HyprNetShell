using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ConfigurationTab(
    WallpaperModuleService wallpapers,
    HistoryStore history,
    TextInputCoordinator inputs) : IMainDialogTab
{
    private readonly Ref<float> _slideshowSwitchAnimation = new(wallpapers.SlideshowEnabled ? 1.0f : 0.0f);
    private readonly ModulesCommon.BoxState _decreaseState = new();
    private readonly ModulesCommon.BoxState _increaseState = new();
    private readonly ModulesCommon.BoxState _notificationDecreaseState = new();
    private readonly ModulesCommon.BoxState _notificationIncreaseState = new();
    private readonly ModulesCommon.BoxState _clipboardDecreaseState = new();
    private readonly ModulesCommon.BoxState _clipboardIncreaseState = new();
    private readonly TextInputCoordinator.Input _wallpaperDirectoryInput = inputs.Create(
        "Wallpaper directory",
        wallpapers.WallpaperDirectory,
        "Directory containing wallpaper images",
        4096,
        wallpapers.SetWallpaperDirectory);

    public string Id => "general";
    public string Title => "General";
    public SvgAsset Icon => Icons.Settings;

    public void Activate()
    {
        inputs.SetValue(_wallpaperDirectoryInput, wallpapers.WallpaperDirectory);
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
            inputs.Build(_wallpaperDirectoryInput),
            MainDialogTabUi.BuildSectionHeader("History settings", "Saved automatically"),
            BuildHistoryControl(
                "Notification history",
                "Maximum saved notifications",
                history.NotificationLimit,
                history.SetNotificationLimit,
                _notificationDecreaseState,
                _notificationIncreaseState),
            BuildHistoryControl(
                "Clipboard history",
                "Maximum saved clipboard entries",
                history.ClipboardLimit,
                history.SetClipboardLimit,
                _clipboardDecreaseState,
                _clipboardIncreaseState),
        ],
    };

    private BoxNode BuildSlideshowToggle()
    {
        var enabled = wallpapers.SlideshowEnabled;
        return new(height: 64)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => wallpapers.SetSlideshowEnabled(!enabled),
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with
            {
                Padding = new Insets(18, 0),
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children =
            [
                new TextNode("Wallpaper slideshow", 16),
                new SwitchNode(enabled, _slideshowSwitchAnimation)
                {
                    OffTrackColor = ThemeManager.Current.Text.MutedColor,
                    OnTrackColor = ThemeManager.Current.Active,
                    KnobColor = ThemeManager.Current.Text,
                },
            ],
        };
    }

    private BoxNode BuildDurationControl()
    {
        var duration = wallpapers.DurationMinutes;
        return new(height: 78)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with
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
                        new TextNode("Slideshow duration", 16),
                        new TextNode("Time between wallpaper changes", color: ThemeManager.Current.Text.MutedColor),
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
                            Children = [new TextNode($"{duration} min", 16)],
                        },
                        BuildDurationButton("+", 1, _increaseState),
                    ],
                },
            ],
        };
    }

    private BoxNode BuildHistoryControl(
        string title,
        string description,
        int value,
        Action<int> setValue,
        ModulesCommon.BoxState decreaseState,
        ModulesCommon.BoxState increaseState) => new(height: 78)
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with
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
                    new TextNode(title, 16),
                    new TextNode(description, color: ThemeManager.Current.Text.MutedColor),
                ],
            },
            new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Center,
                Style = new Style { Spacing = 8 },
                Children =
                [
                    BuildValueButton("-", () => setValue(value - HistoryStore.LimitStep), decreaseState),
                    new BoxNode(92, 34)
                    {
                        HorizontalAlignment = ItemsAlignment.Center,
                        VerticalAlignment = ItemsAlignment.Center,
                        Children = [new TextNode(value.ToString(), 16)],
                    },
                    BuildValueButton("+", () => setValue(value + HistoryStore.LimitStep), increaseState),
                ],
            },
        ],
        };

    private BoxNode BuildValueButton(string label, Action onClick, ModulesCommon.BoxState buttonState)
    {
        var state = buttonState.UpdateColor(ThemeManager.Current.Text.MutedColor);
        return new BoxNode(38, 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = onClick,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(label, 20)],
        };
    }

    private BoxNode BuildDurationButton(string label, int delta, ModulesCommon.BoxState buttonState)
    {
        var state = buttonState.UpdateColor(ThemeManager.Current.Text.MutedColor);
        return new(38, 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => wallpapers.SetDurationMinutes(wallpapers.DurationMinutes + delta),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
            },
            Children = [new TextNode(label, 20)],
        };
    }
}

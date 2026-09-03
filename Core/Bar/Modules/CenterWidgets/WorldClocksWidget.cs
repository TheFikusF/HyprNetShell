using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class WorldClocksWidget
{
    public const int WIDTH = 220;

    private readonly Theme _theme;
    private readonly ModulesCommon.BoxState _titleState = new();
    private readonly WorldClockService _clocks;
    private readonly Dictionary<string, ModulesCommon.BoxState> _dateCopyButtons = new();

    public WorldClocksWidget(Theme theme)
        : this(theme, WorldClockService.Shared)
    {
    }

    public WorldClocksWidget(Theme theme, WorldClockService clocks)
    {
        _theme = theme;
        _clocks = clocks;
    }

    public Node Draw(DateTime now, Action? openClocks = null)
    {
        var state = _titleState.UpdateColor(_theme.Panel);
        return new BoxNode(WIDTH)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 8,
            },
            Children =
        [
            new BoxNode(height: 34)
            {
                VerticalAlignment = ItemsAlignment.Center,
                HorizontalAlignment = ItemsAlignment.Center,
                OnClick = openClocks,
                IsHovered = _titleState,
                Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
                {
                    Padding = 0,
                    BorderRadius = 8,
                    BorderWidth = 0,
                    Spacing = 8,
                },
                Children =
                [
                    new ImageNode(Icons.Clock, 22, 22, _theme.Text),
                    new TextNode("World clocks", 22, _theme.Text)
                ]
            },
            BuildRow("Local", now),
            .. _clocks.SelectedClocks.Select(clock =>
                BuildRow(clock.DisplayName, WorldClockService.GetTime(clock, now.ToUniversalTime()))),
        ],
        };
    }

    private BoxNode BuildRow(string label, DateTime time)
    {
        var state = _dateCopyButtons.GetState(label, _theme.Panel).UpdateColor(_theme.Panel);
        return new BoxNode(Style.Empty, ItemsAlignment.Spread, ItemsAlignment.Center)
        {
            new TextNode(label, _theme.Text, _theme.Text),
            new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
            {
                new TextNode(time.ToString("HH:mm"), _theme.Text, _theme.Text),
                new BoxNode
                {
                    IsHovered = state.Hovered,
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    OnClick = () => Utils.CopyToClipboard($"{label} - {time:HH:mm}"),
                    Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
                    {
                        Padding = 4,
                        BorderRadius = 8,
                        BorderWidth = 0,
                    },
                    Children = [new ImageNode(Icons.Copy, 14, 14, _theme.Text)]
                }
            }
        };
    }

}

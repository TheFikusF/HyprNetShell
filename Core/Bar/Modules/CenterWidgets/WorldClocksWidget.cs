using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;

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
    private readonly ClipboardHistoryService _clipboard;
    private readonly Dictionary<string, ModulesCommon.BoxState> _dateCopyButtons = new();

    public WorldClocksWidget(Theme theme, ClipboardHistoryService clipboard)
        : this(theme, WorldClockService.Shared, clipboard)
    {
    }

    public WorldClocksWidget(
        Theme theme,
        WorldClockService clocks,
        ClipboardHistoryService clipboard)
    {
        _theme = theme;
        _clocks = clocks;
        _clipboard = clipboard;
    }

    public Node Draw(DateTime now, Action? openClocks = null) => new BoxNode(WIDTH)
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
            ModulesCommon.CentralWidgetHeader(Icons.Clock, "World clocks", openClocks, _titleState, _theme),
            BuildRow("Local", now),
            .. _clocks.SelectedClocks.Select(clock =>
                BuildRow(clock.DisplayName, WorldClockService.GetTime(clock, now.ToUniversalTime()))),
        ],
    };

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
                    OnClick = () => _ = _clipboard.CopyTextAsync($"{label} - {time:HH:mm}"),
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

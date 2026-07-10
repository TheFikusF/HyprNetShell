using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal sealed class TrayModule(
    Func<IReadOnlyList<TrayItemSnapshot>> snapshot,
    SniTrayService service,
    StatusBarTheme theme) : IDrawableModule
{
    private readonly Dictionary<string, ModulesCommon.NodeWithPopup> _nodes = [];
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];

    public Node Draw()
    {
        var items = snapshot();

        return items.Count != 0
            ? new BoxNode
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Center,
                Children = items.Select((item, index) => BuildItem(item, index == 0, index == items.Count - 1))
                    .ToArray(),
            }
            : new SpacerNode();
    }

    private Node BuildItem(TrayItemSnapshot item, bool left, bool right)
    {
        if (_nodes.TryGetValue(item.Id, out var node) == false)
        {
            node = new ModulesCommon.NodeWithPopup($"tray_module_{item.Id}")
                { HorizontalAlignment = ItemsAlignment.End };
            _nodes[item.Id] = node;
        }

        var icon = string.IsNullOrWhiteSpace(item.IconPath)
            ? ModulesCommon.BuildBadge(ModulesCommon.AppBadge(item.Title), 14.0f, theme.Panel, theme)
            : new ImageNode(item.IconPath, 20, 20);

        return node.Draw([
            new BoxNode(32, 32)
            {
                Direction = Direction.Horizontal,
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Style = ModulesCommon.ModuleStyle(theme, theme.Panel, left, right) with
                {
                    Padding = new Insets(4),
                },
                Children = [icon],
            }
        ], () => BuildPopup(item));
    }

    private Node BuildPopup(TrayItemSnapshot item)
    {
        var rows = item.Menu?.Rows;
        return new BoxNode(300)
        {
            IgnoreLayout = true,
            Style = new Style { Padding = new Insets(28, 0, 0, 0) },
            Children =
            [
                new BoxNode(300)
                {
                    Direction = Direction.Vertical,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Style = new Style
                    {
                        BackgroundColor = Color.FromRgb(0, 0, 0, 0.94f),
                        BorderColor = theme.Border,
                        BorderRadius = 8,
                        BorderWidth = 2,
                        Padding = 8,
                        Spacing = 4,
                    },
                    Children = rows is { Count: > 0 }
                        ? rows.Select(row => BuildRow(item, row)).ToArray()
                        : [new TextNode(item.Title, 14.0f, theme.Muted)],
                },
            ],
        };
    }

    private Node BuildRow(TrayItemSnapshot item, PopupRowSnapshot row)
    {
        if (row.Kind == PopupRowKind.Separator)
        {
            return ModulesCommon.BuildDivider(theme.Border, height: 12);
        }

        var key = item.Id + ":" + (row.ActionId?.ToString() ?? row.Label);
        if (!_rowStates.TryGetValue(key, out var state))
        {
            state = new ModulesCommon.BoxState(theme.Panel);
            _rowStates[key] = state;
        }

        var target = state.Hovered && row.Enabled ? Color.Lighten(theme.Panel, 0.12f) : theme.Panel;
        state.Background = Color.LerpSmooth(state.Background, target, 18, ModulesCommon.DELTA_TIME);

        return new BoxNode(height: 30)
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = row is { Enabled: true, ActionId: { } actionId }
                ? () => _ = service.TriggerMenuActionAsync(item, actionId)
                : null,
            Style = new Style
            {
                BackgroundColor = state.Background,
                BorderRadius = 6,
                Padding = new Insets(8, 4),
            },
            Children = [new TextNode(row.Label, 14.0f, row.Enabled ? theme.Text : theme.Muted)],
        };
    }
}
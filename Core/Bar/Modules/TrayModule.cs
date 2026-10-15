using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Renderers;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class TrayModule(
    SniTrayService service, PopupCoordinator popupCoordinator) : IDrawableModule
{
    private const float EXPANSION_ANIMATION_DECAY = 18.0f;
    private const int ITEM_WIDTH = 30;

    private readonly Ref<bool> _hovered = new();
    private readonly Dictionary<string, NodeWithPopup> _nodes = [];
    private readonly Dictionary<string, ModulesCommon.BoxState> _rowStates = [];

    private float _expansion;

    public Node Draw()
    {
        var items = service.Snapshot;
        if (items.Count == 0)
        {
            _expansion = 0;
            _hovered.Value = false;
            return new SpacerNode();
        }

        var expanded = _hovered.Value || items.Any(item =>
            _nodes.TryGetValue(item.Id, out var node) && node.IsHovered);
        var target = expanded ? 1.0f : 0.0f;
        _expansion = PrimitivesMath.LerpSmooth(_expansion, target, EXPANSION_ANIMATION_DECAY, Renderer.DeltaTime);
        if (MathF.Abs(_expansion - target) < 0.001f)
        {
            _expansion = target;
        }

        return new BoxNode {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHoveredThrough = _hovered,
            Style = new Style {
                BackgroundColor = Color.FromRgb(0, 0, 0, 0.9f),
                Padding = new Insets(8, 4, 8, 4),
                BorderRadius = new BorderRadius(ThemeManager.Current.Border.Radius, 0, 0, ThemeManager.Current.Border.Radius),
                ShadowColor = Color.Black with { A = 0.45f },
                ShadowDistance = 5.0f
            },
            Children = [
                new ImageNode(Icons.ChevronLeft, 18, 18, ThemeManager.Current.Text),
                ..items.Select(BuildItem),
            ],
        };
    }

    private Node BuildItem(TrayItemSnapshot item)
    {
        if (_nodes.TryGetValue(item.Id, out var node) == false)
        {
            node = new NodeWithPopup(popupCoordinator, $"tray_module_{item.Id}") {
                HorizontalAlignment = ItemsAlignment.End,
                TopOffset = 24,
            };
            _nodes[item.Id] = node;
        }

        var icon = string.IsNullOrWhiteSpace(item.IconPath)
            ? ModulesCommon.BuildBadge(ModulesCommon.AppBadge(item.Title), ThemeManager.Current.Panel)
            : new ImageNode(item.IconPath, 18, 18);

        var width = (int)MathF.Round(ITEM_WIDTH * _expansion);
        if (width == 0)
        {
            return new SpacerNode();
        }

        return node.Draw([
            new BoxNode(width, 18)
            {
                Opacity = _expansion,
                Direction = Direction.Horizontal,
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Children = width >= 18 ? [icon] : [],
            }
        ], () => BuildPopup(item));
    }

    private BoxNode BuildPopup(TrayItemSnapshot item)
    {
        var rows = item.Menu?.Rows;
        return new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.PopupStyle(),
            Children = rows is { Count: > 0 }
                ? rows.Select(row => BuildRow(item, row)).ToArray()
                : [new TextNode(item.Title, color: ThemeManager.Current.Text.MutedColor)],
        };
    }

    private Node BuildRow(TrayItemSnapshot item, PopupRowSnapshot row)
    {
        if (row.Kind == PopupRowKind.Separator)
        {
            return ModulesCommon.BuildDivider(ThemeManager.Current.Border, height: 12);
        }

        var state = _rowStates.GetState(item.Id + ":" + (row.ActionId?.ToString() ?? row.Label), ThemeManager.Current.Panel);
        var target = state.Hovered && row.Enabled ? Color.Lighten(ThemeManager.Current.Panel, 0.12f) : ThemeManager.Current.Panel;
        state.Background = Color.LerpSmooth(state.Background, target, 18, Renderer.DeltaTime);

        return new BoxNode(height: 30) {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = row is { Enabled: true, ActionId: { } actionId }
                ? () => _ = service.TriggerMenuActionAsync(item, actionId)
                : null,
            Style = new Style {
                BackgroundColor = state.Background,
                BorderRadius = 6,
                Padding = new Insets(8, 4),
            },
            Children = [new TextNode(row.Label, color: row.Enabled ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor)],
        };
    }
}

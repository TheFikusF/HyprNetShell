using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

internal sealed class SegmentedSwitch
{
    internal readonly record struct Item(string Id, Node Content);

    private readonly Dictionary<string, ModulesCommon.BoxState> _states = [];

    public BoxNode Build(
        Theme theme,
        IReadOnlyList<Item> items,
        string selectedId,
        Action<string> onSelected) => new()
    {
        Direction = Direction.Horizontal,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            ..items.Select((item, index) => BuildItem(theme, item, selectedId, onSelected, index, items.Count)),
        ],
    };

    private BoxNode BuildItem(
        Theme theme,
        Item item,
        string selectedId,
        Action<string> onSelected,
        int index,
        int count)
    {
        var selected = item.Id.Equals(selectedId, StringComparison.Ordinal);
        var normal = selected ? theme.Active : theme.Panel;
        var state = _states.GetState(item.Id, normal).UpdateColor(normal);

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = selected ? null : () => onSelected(item.Id),
            Style = ModulesCommon.ModuleStyle(theme, state.Background, index == 0, index == count - 1) with
            {
                BorderWidth = selected ? theme.Border.Width : 0,
                Padding = new Insets(8, 6),
            },
            Children = [item.Content],
        };
    }
}

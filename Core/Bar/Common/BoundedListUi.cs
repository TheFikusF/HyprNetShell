using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;

namespace HyprNetShell.Core.Bar.Common;

internal static class BoundedListUi
{
    public const int DEFAULT_VISIBLE_ITEM_COUNT = 7;

    public static void MoveSelection(
        ref int selectedIndex,
        ref int firstIndex,
        int direction,
        int itemCount,
        int visibleItemCount = DEFAULT_VISIBLE_ITEM_COUNT)
    {
        if (itemCount <= 0)
        {
            selectedIndex = 0;
            firstIndex = 0;
            return;
        }

        selectedIndex = PositiveModulo(selectedIndex + direction, itemCount);
        AlignViewport(ref firstIndex, selectedIndex, itemCount, visibleItemCount);
    }

    public static void Normalize(
        ref int selectedIndex,
        ref int firstIndex,
        int itemCount,
        int visibleItemCount = DEFAULT_VISIBLE_ITEM_COUNT)
    {
        if (itemCount <= 0)
        {
            selectedIndex = 0;
            firstIndex = 0;
            return;
        }

        selectedIndex = Math.Clamp(selectedIndex, 0, itemCount - 1);
        AlignViewport(ref firstIndex, selectedIndex, itemCount, visibleItemCount);
    }

    public static void MoveViewport(
        ref int firstIndex,
        int direction,
        int itemCount,
        int visibleItemCount = DEFAULT_VISIBLE_ITEM_COUNT)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleItemCount, 1);
        firstIndex = Math.Clamp(
            firstIndex + Math.Sign(direction),
            0,
            Math.Max(0, itemCount - visibleItemCount));
    }

    public static void NormalizeViewport(
        ref int firstIndex,
        int itemCount,
        int visibleItemCount = DEFAULT_VISIBLE_ITEM_COUNT)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleItemCount, 1);
        firstIndex = Math.Clamp(firstIndex, 0, Math.Max(0, itemCount - visibleItemCount));
    }

    public static IEnumerable<(T Item, int Index)> VisibleItems<T>(
        this IReadOnlyCollection<T> items,
        int firstIndex,
        int visibleItemCount = DEFAULT_VISIBLE_ITEM_COUNT)
    {
        var start = Math.Clamp(firstIndex, 0, Math.Max(0, items.Count - 1));
        return items
            .Skip(start)
            .Take(visibleItemCount)
            .Select((item, visibleIndex) => (item, start + visibleIndex));
    }

    public static Node BuildScrollableResults(
        Node content,
        int firstItem,
        int totalItems,
        int visibleItems, Action<float>? onScroll = null)
    {
        if (totalItems <= visibleItems)
        {
            return content;
        }

        return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Stretch,
            OnScroll = onScroll,
            VerticalAlignment = ItemsAlignment.Start,
            Style = Style.Spacer,
            Children =
            [
                content,
                new ScrollbarNode(
                    content.Height,
                    firstItem,
                    totalItems,
                    visibleItems,
                    ThemeManager.Current.Panel,
                    ThemeManager.Current.Text.MutedColor),
            ],
        };
    }

    public static Node BuildList<T>(
        IReadOnlyCollection<T> items,
        Func<T, int, Node> renderItem,
        int firstItem,
        int visibleItems, Action<float>? onScroll = null) => BuildScrollableResults(new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = Style.Spacer,
            Children = [..items.VisibleItems(firstItem, visibleItems)
                .Select(item => renderItem(item.Item, item.Index))],
        }, firstItem, items.Count, visibleItems, onScroll);

    private static void AlignViewport(
        ref int firstIndex,
        int selectedIndex,
        int itemCount,
        int visibleItemCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(visibleItemCount, 1);
        if (selectedIndex < firstIndex)
        {
            firstIndex = selectedIndex;
        }
        else if (selectedIndex >= firstIndex + visibleItemCount)
        {
            firstIndex = selectedIndex - visibleItemCount + 1;
        }

        firstIndex = Math.Clamp(firstIndex, 0, Math.Max(0, itemCount - visibleItemCount));
    }

    private static int PositiveModulo(int value, int divisor) => (value % divisor + divisor) % divisor;
}

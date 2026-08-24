using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;

namespace HyprNetShell.Core.Bar.Common;

internal static class BoundedListUi
{
    public const int DefaultVisibleItemCount = 7;

    public static void MoveSelection(
        ref int selectedIndex,
        ref int firstIndex,
        int direction,
        int itemCount,
        int visibleItemCount = DefaultVisibleItemCount)
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
        int visibleItemCount = DefaultVisibleItemCount)
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

    public static IEnumerable<(T Item, int Index)> VisibleItems<T>(
        this IReadOnlyList<T> items,
        int firstIndex,
        int visibleItemCount = DefaultVisibleItemCount)
    {
        var start = Math.Clamp(firstIndex, 0, Math.Max(0, items.Count - 1));
        return items
            .Skip(start)
            .Take(visibleItemCount)
            .Select((item, visibleIndex) => (item, start + visibleIndex));
    }

    public static Node BuildScrollableResults(
        BoxNode content,
        int firstItem,
        int totalItems,
        int visibleItems,
        Theme theme)
    {
        if (totalItems <= visibleItems)
        {
            return content;
        }

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            Style = new Style { Spacing = 8 },
            Children =
            [
                content,
                new ScrollbarNode(
                    content.Height,
                    firstItem,
                    totalItems,
                    visibleItems,
                    theme.Panel,
                    theme.Muted),
            ],
        };
    }

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

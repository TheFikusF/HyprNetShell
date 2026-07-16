using System.Globalization;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal static class MainDialogTabUi
{
    public const int VISIBLE_ROW_COUNT = 7;

    public static Node BuildSectionHeader(string title, string status) => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            new TextNode(title, 22, Theme.Default.Text),
            new TextNode(status, Theme.Default.TextSize, Theme.Default.Muted),
        ],
    };

    public static Node BuildInput(string value, string placeholder) => new BoxNode(height: 46)
    {
        VerticalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(Theme.Default, Theme.Default.Panel) with
        {
            BorderRadius = 8,
            Padding = new Insets(Theme.Default.TextSize, 8),
        },
        Children =
        [
            new TextNode(value.Length == 0 ? placeholder : value + (Math.Sin(Environment.TickCount64 / 200) > 0 ? "|" : ""),
                16, value.Length == 0 ? Theme.Default.Muted : Theme.Default.Text),
        ],
    };

    public static Node BuildScrollableResults(
        BoxNode content,
        int firstItem,
        int totalItems,
        int visibleItems)
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
                    Theme.Default.Panel,
                    Theme.Default.Muted),
            ],
        };
    }

    public static string ResultCount(int selectedIndex, int count, string emptyText) =>
        count == 0 ? emptyText : $"{selectedIndex + 1} / {count}";

    public static string RemoveLastTextElement(string value)
    {
        var indexes = StringInfo.ParseCombiningCharacters(value);
        return indexes.Length <= 1 ? "" : value[..indexes[^1]];
    }

    public static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 3)] + "...";

    public static void MoveSelection(ref int selectedIndex, ref int firstIndex, int direction, int itemCount)
    {
        if (itemCount == 0)
        {
            return;
        }

        selectedIndex = (itemCount + selectedIndex + direction) % (itemCount);
        if (selectedIndex < firstIndex)
        {
            firstIndex = selectedIndex;
        }
        else if (selectedIndex >= firstIndex + VISIBLE_ROW_COUNT)
        {
            firstIndex = selectedIndex - VISIBLE_ROW_COUNT + 1;
        }
    }
}

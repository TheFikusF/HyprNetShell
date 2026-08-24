using System.Globalization;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal static class MainDialogTabUi
{


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



    public static string ResultCount(int selectedIndex, int count, string emptyText) =>
        count == 0 ? emptyText : $"{selectedIndex + 1} / {count}";

    public static string RemoveLastTextElement(string value)
    {
        var indexes = StringInfo.ParseCombiningCharacters(value);
        return indexes.Length <= 1 ? "" : value[..indexes[^1]];
    }

    public static string Trim(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..(maximumLength - 3)] + "...";


}

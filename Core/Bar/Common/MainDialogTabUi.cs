using System.Globalization;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

internal static class MainDialogTabUi
{
    public static Node BuildSectionHeader(string title, string status) => new BoxNode(new Style { Spacing = 12 }, ItemsAlignment.Spread, ItemsAlignment.Center)
    {
        new TextNode(title, 22),
        new TextNode(status, color: ThemeManager.Current.Text.MutedColor),
    };


    public static BoxNode BuildButton(IDictionary<string, ModulesCommon.BoxState> states,
        string text,
        string key,
        Action? action)
    {
        if (!states.TryGetValue(key, out var state))
        {
            state = new ModulesCommon.BoxState { Background = ThemeManager.Current.Panel };
            states[key] = state;
        }

        state.UpdateColor(ThemeManager.Current.Panel);
        return new BoxNode
        {
            IsHovered = state.Hovered,
            OnClick = action,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
                Padding = new Insets(10, 6),
            },
            Children = [new TextNode(text, 13, action is null ? ThemeManager.Current.Text.MutedColor : ThemeManager.Current.Text)],
        };
    }

    public static Node BuildStatus(string? status) => string.IsNullOrWhiteSpace(status)
        ? new SpacerNode()
        : new TextNode(status, color: ThemeManager.Current.Text.MutedColor, maxWidth: 820);

    public static BoxNode BuildMessage(string message) => new(height: 52)
    {
        VerticalAlignment = ItemsAlignment.Center,
        HorizontalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with { BorderRadius = 8, BorderWidth = 0 },
        Children = [new TextNode(message, color: ThemeManager.Current.Text.MutedColor)],
    };

    public static string ResultCount(int selectedIndex, int count, string emptyText) =>
        count == 0 ? emptyText : $"{selectedIndex + 1} / {count}";

    public static string RemoveLastTextElement(string value)
    {
        var indexes = StringInfo.ParseCombiningCharacters(value);
        return indexes.Length <= 1 ? "" : value[..indexes[^1]];
    }

}

using System.Globalization;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

internal static class MainDialogTabUi
{
    public static Node BuildSectionHeader(string title, string status) => new BoxNode(new Style { Spacing = 12 }, ItemsAlignment.Spread, ItemsAlignment.Center)
    {
        new TextNode(title, 22, Theme.Default.Text),
        new TextNode(status, Theme.Default.Text.Size, Theme.Default.Text.MutedColor),
    };

    public static Node BuildInput(string value, string placeholder) => new BoxNode(height: 46)
    {
        VerticalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(Theme.Default, Theme.Default.Panel) with
        {
            BorderRadius = 8,
            Padding = new Insets(Theme.Default.Text.Size, 8),
        },
        Children =
        [
            new TextNode(value.Length == 0 ? placeholder : value + (Math.Sin(Environment.TickCount64 / 200) > 0 ? "|" : ""),
                16, value.Length == 0 ? Theme.Default.Text.MutedColor : Theme.Default.Text),
        ],
    };

    public static BoxNode BuildButton(
        Theme theme,
        IDictionary<string, ModulesCommon.BoxState> states,
        string text,
        string key,
        Action? action)
    {
        if (!states.TryGetValue(key, out var state))
        {
            state = new ModulesCommon.BoxState { Background = theme.Panel };
            states[key] = state;
        }

        state.UpdateColor(theme.Panel);
        return new BoxNode
        {
            IsHovered = state.Hovered,
            OnClick = action,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
                Padding = new Insets(10, 6),
            },
            Children = [new TextNode(text, 13, action is null ? theme.Text.MutedColor : theme.Text)],
        };
    }

    public static Node BuildStatus(Theme theme, string? status) => string.IsNullOrWhiteSpace(status)
        ? new SpacerNode()
        : new TextNode(status, theme.Text, theme.Text.MutedColor, maxWidth: 820);

    public static BoxNode BuildMessage(Theme theme, string message) => new(height: 52)
    {
        VerticalAlignment = ItemsAlignment.Center,
        HorizontalAlignment = ItemsAlignment.Center,
        Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with { BorderRadius = 8, BorderWidth = 0 },
        Children = [new TextNode(message, theme.Text, theme.Text.MutedColor)],
    };

    public static string ResultCount(int selectedIndex, int count, string emptyText) =>
        count == 0 ? emptyText : $"{selectedIndex + 1} / {count}";

    public static string RemoveLastTextElement(string value)
    {
        var indexes = StringInfo.ParseCombiningCharacters(value);
        return indexes.Length <= 1 ? "" : value[..indexes[^1]];
    }

}

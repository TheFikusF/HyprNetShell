using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

internal static class PasswordFieldView
{
    private const int MAX_VISIBLE_MASK_LENGTH = 64;

    internal static BoxNode Build(int passwordLength, bool hasError = false)
    {
        var theme = ThemeManager.Current;
        var bullets = new string('•', Math.Clamp(passwordLength, 0, MAX_VISIBLE_MASK_LENGTH));
        return new BoxNode(360, 52) {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(Color.Black with { A = 0.48f }) with {
                BorderColor = hasError ? theme.Critical : theme.Border,
                BorderRadius = 10,
                Padding = new Insets(12, 8),
                ShadowColor = null,
            },
            Children =
            [
                new TextNode(
                    bullets.Length == 0 ? "Password" : bullets,
                    20,
                    bullets.Length == 0 ? theme.Text.MutedColor : theme.Text)
            ],
        };
    }
}

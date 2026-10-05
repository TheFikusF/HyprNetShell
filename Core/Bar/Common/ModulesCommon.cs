using HyprNetShell.GUI;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

public static class ModulesCommon
{
    private static readonly AppIconResolver IconResolver = new();

    public static Color ToBackground(Color color) => Color.Lerp(ThemeManager.Current.Panel, color, 0.125f) with { A = 0.9f };

    public static Node BuildDivider(Color color, int? width = null, int height = 24) => new BoxNode(width, height)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Center,
        Children = [new BoxNode(height: 1) { Style = new Style { BackgroundColor = color } }]
    };

    public static Node BuildTextWithIcon(SvgAsset icon, string text, Color? color = null,
        Style style = default, int? width = null, int? maxTextWidth = null) =>
        new BoxNode(width)
        {
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = style with { Spacing = 8 },
            Children =
            [
                new ImageNode(icon, 18, 18, color ?? ThemeManager.Current.Text),
                new TextNode(text, color: color, maxWidth: maxTextWidth, wrapping: maxTextWidth.HasValue ? TextWrapping.Ellipsis : TextWrapping.NoWrap),
            ],
        };

    public static Node BuildBadge(string text, Color fill) => new BoxNode(14, 14)
    {
        Direction = Direction.Horizontal,
        HorizontalAlignment = ItemsAlignment.Center,
        VerticalAlignment = ItemsAlignment.Center,
        Style = new Style { BackgroundColor = fill, BorderRadius = new BorderRadius(ThemeManager.Current.Border.Radius) },
        Children = { new TextNode(text, 8) },
    };

    public static Node BuildAppBadge(string className, int iconSize, Color fill)
    {
        var imagePath = IconResolver.TryResolve(className);
        return imagePath is null
            ? BuildBadge(AppBadge(className), fill)
            : new ImageNode(imagePath, iconSize, iconSize);
    }

    public static Style ModuleStyle(Color fill, bool left = true, bool right = true) => new()
    {
        BackgroundColor = fill,
        BorderRadius = new BorderRadius(left ? ThemeManager.Current.Border.Radius : 0, right ? ThemeManager.Current.Border.Radius : 0,
            right ? ThemeManager.Current.Border.Radius : 0, left ? ThemeManager.Current.Border.Radius : 0),
        BorderWidth = new Insets(ThemeManager.Current.Border.Width, right ? ThemeManager.Current.Border.Width : 0,
            ThemeManager.Current.Border.Width, left ? ThemeManager.Current.Border.Width : 0),
        Padding = new Insets(8, 6),
        ShadowColor = Color.Black with { A = 0.45f },
        ShadowDistance = 4.0f
    };

    public static Style PopupStyle() => ModuleStyle(Color.FromRgb(0, 0, 0, 0.85f)) with
    {
        BorderRadius = 8,
        Padding = 8,
        Spacing = 8,
        ShadowColor = Color.Black with { A = 0.65f },
        ShadowDistance = 8.0f
    };

    public static string AppBadge(string className)
    {
        className = className.Trim();
        return string.IsNullOrWhiteSpace(className) ? "?" : className[..1].ToUpperInvariant();
    }

    public static BoxNode CentralWidgetHeader(SvgAsset icon, string text, Action? onClick, BoxState state)
    {
        state.UpdateColor(ThemeManager.Current.Panel);
        return new(height: 34)
        {
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            OnClick = onClick,
            IsHovered = onClick is null ? null : state.Hovered,
            Style = ModuleStyle(state.Background) with
            {
                Padding = new Insets(10, 0),
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children =
            [
                new ImageNode(icon, 22, 22, ThemeManager.Current.Text),
                new TextNode(text, 22),
            ],
        };
    }

    public static TState GetState<TKey, TState>(this IDictionary<TKey, TState> stateDictionary, TKey key,
        Color initialColor)
        where TState : BoxState, new()
        where TKey : notnull
    {
        if (stateDictionary.TryGetValue(key, out var state))
        {
            return state;
        }

        state = new TState { Background = initialColor };
        stateDictionary[key] = state;
        return state;
    }

    public static TState UpdateColor<TState>(this TState state, Color color) where TState : BoxState, new()
    {
        var target = state.Hovered ? Color.Lighten(color, 0.18f) : color;
        state.Background = Color.LerpSmooth(state.Background, target, 18.0f, Renderer.DeltaTime);
        return state;
    }

    public class BoxState
    {
        public Ref<bool> Hovered { get; } = new();
        public Color Background { get; set; }

        public static implicit operator Color(BoxState state) => state.Background;
        public static implicit operator Ref<bool>(BoxState state) => state.Hovered;
    }
}

using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

public class ActionButton
{
    public readonly record struct ButtonStyle
    {
        public Color Background { get; init; }
        public Color Foreground { get; init; }
        public Color DisabledForeground { get; init; }
        public Insets Padding { get; init; }
        public BorderRadius BorderRadius { get; init; }
        public int Spacing { get; init; }
        public int IconSize { get; init; }
        public int? TextSize { get; init; }
        public float DisabledOpacity { get; init; }
        public bool HoverWhenDisabled { get; init; }

        public static ButtonStyle Default => new() {
            Background = ThemeManager.Current.Panel,
            Foreground = ThemeManager.Current.Text,
            DisabledForeground = ThemeManager.Current.Text,
            Padding = new Insets(12, 8),
            BorderRadius = 8,
            Spacing = 8,
            IconSize = 16,
            DisabledOpacity = 0.5f,
        };
    }

    private readonly ModulesCommon.BoxState _state = new();
    private ButtonStyle? _style;
    private bool _initialized;

    public SvgAsset? Icon { get; set; }
    public string? Text { get; set; }
    public Action? Action { get; set; }
    public bool IsEnabled { get; set; } = true;
    public float Flex { get; set; }
    public ButtonStyle Style
    {
        get => _style ?? ButtonStyle.Default;
        set => _style = value;
    }

    public static BoxNode Build<TKey, TButton>(
        IDictionary<TKey, TButton> buttons,
        TKey key,
        string? text,
        Action action,
        SvgAsset? icon = null,
        bool isEnabled = true,
        ButtonStyle? style = null,
        float flex = 0)
        where TKey : notnull
        where TButton : ActionButton, new()
    {
        if (!buttons.TryGetValue(key, out var button))
        {
            button = new TButton();
            buttons[key] = button;
        }

        button.Text = text;
        button.Icon = icon;
        button.Action = action;
        button.IsEnabled = isEnabled;
        button._style = style;
        button.Flex = flex;
        return button.Build();
    }

    public BoxNode Build()
    {
        var style = Style;
        if (!_initialized)
        {
            _state.Background = style.Background;
            _initialized = true;
        }

        if (!IsEnabled && !style.HoverWhenDisabled)
        {
            _state.Hovered.Value = false;
        }

        _state.UpdateColor(style.Background);
        var foreground = IsEnabled ? style.Foreground : style.DisabledForeground;
        var children = new List<Node>();
        if (Icon is { } icon)
        {
            children.Add(new ImageNode(icon, style.IconSize, style.IconSize, foreground));
        }

        if (Text is { } text)
        {
            children.Add(new TextNode(text, color: foreground));
            if (style.TextSize is { } textSize)
            {
                children[^1] = new TextNode(text, textSize, foreground);
            }
        }

        return new BoxNode {
            Flex = Flex,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = IsEnabled || style.HoverWhenDisabled ? _state.Hovered : null,
            OnClick = IsEnabled ? Invoke : null,
            Opacity = IsEnabled ? 1.0f : style.DisabledOpacity,
            Style = ModulesCommon.ModuleStyle(_state.Background) with {
                Padding = style.Padding,
                BorderRadius = style.BorderRadius,
                BorderWidth = 0,
                Spacing = style.Spacing,
            },
            Children = children,
        };
    }

    private void Invoke()
    {
        if (IsEnabled)
        {
            Action?.Invoke();
        }
    }
}

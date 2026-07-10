using System.Runtime.CompilerServices;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

public static class ModulesCommon
{
    public class NodeWithPopup
    {
        private static string? _lastOpenedId;
        private static string? _pendingOpenedId;

        private readonly string _moduleId;
        private readonly bool _ignorePopupQueue;
        public ItemsAlignment HorizontalAlignment { get; init; }
        public Func<bool, bool> GetShouldShowPopup { get; init; } = hovered => hovered;
        
        private readonly RefBool _hovered = new();
        
        public bool IsHovered => _hovered.Value;
        public bool ShouldShowPopup => GetShouldShowPopup(IsHovered);

        public NodeWithPopup(string moduleId = "", bool ignorePopupQueue = false)
        {
            _moduleId = moduleId;
            _ignorePopupQueue = ignorePopupQueue;
        }

        static NodeWithPopup()
        {
            Renderer.OnFrameStart += () => { };
            Renderer.OnFrameEnd += () =>
            {
                _lastOpenedId = _pendingOpenedId;
                _pendingOpenedId = null;
            };
        }

        public Node Draw(ICollection<Node> module, Func<Node> popup)
        {
            var shouldShowExternal = GetShouldShowPopup(IsHovered);
            var shouldShow = shouldShowExternal && (_lastOpenedId == _moduleId || _ignorePopupQueue);
            if (_ignorePopupQueue == false && shouldShowExternal && (_lastOpenedId != _moduleId || string.IsNullOrEmpty(_pendingOpenedId)))
            {
                _pendingOpenedId = _moduleId;
            }
            
            return new BoxNode
            {
                Direction = Direction.Horizontal,
                VerticalAlignment = ItemsAlignment.Start,
                HorizontalAlignment = HorizontalAlignment,
                IsHoveredThrough = _hovered,
                Children =
                [
                    new BoxNode
                    {
                        VerticalAlignment = ItemsAlignment.Center,
                        Children = module
                    },
                    shouldShow ? popup() : new SpacerNode()
                ],
            };
        }
    }

    public const float DELTA_TIME = 1.0f / 60.0f;

    private static readonly AppIconResolver IconResolver = new();

    public static Node BuildDivider(Color color, int? width = null, int height = 24) =>
        new BoxNode(width, height)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Children =
            [
                new BoxNode(height: 2)
                {
                    Style = new Style { BackgroundColor = color }
                }
            ]
        };

    public static Node BuildBadge(string text, float fontSize, Color fill, StatusBarTheme theme)
    {
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { BackgroundColor = fill, BorderRadius = new BorderRadius(theme.Radius) },
            Children = { new TextNode(text, fontSize, theme.Text) },
        };
    }

    public static Node BuildAppBadge(string className, int iconSize, Color fill, StatusBarTheme theme)
    {
        var imagePath = IconResolver.TryResolve(className);
        if (imagePath is null)
        {
            return BuildBadge(AppBadge(className), MathF.Max(8.0f, iconSize - 6.0f), fill, theme);
        }

        return new ImageNode(imagePath, iconSize, iconSize);
    }

    public static Style ModuleStyle(StatusBarTheme theme, Color fill, bool left = true, bool right = true)
    {
        return new Style
        {
            BackgroundColor = fill,
            BorderColor = theme.Border,
            BorderRadius = new BorderRadius(left ? theme.Radius : 0, right ? theme.Radius : 0,
                right ? theme.Radius : 0, left ? theme.Radius : 0),
            BorderWidth = new Insets(theme.BorderWidth, right ? theme.BorderWidth : 0,
                theme.BorderWidth, left ? theme.BorderWidth : 0),
            Padding = new Insets(8, 6)
        };
    }

    public static string AppBadge(string className)
    {
        className = className.Trim();
        return string.IsNullOrWhiteSpace(className) ? "?" : className[..1].ToUpperInvariant();
    }

    public sealed class BoxState(Color background)
    {
        public RefBool Hovered { get; } = new();
        public Color Background { get; set; } = background;
    }
}
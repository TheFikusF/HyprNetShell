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
        private static readonly Dictionary<string, DateTime> CantOpenBefore = new();

        private readonly string _moduleId;
        private readonly bool _ignorePopupQueue;

        private float _popupOpacity = 0f;

        public int TopOffset { get; init; } = 32;
        public ItemsAlignment HorizontalAlignment { get; init; }
        public Func<bool, bool> GetShouldShowPopup { get; init; } = hovered => hovered;

        private readonly RefBool _hovered = new();

        public bool IsHovered => _hovered.Value;
        public bool ShouldShowPopup => GetShouldShowPopup(IsHovered);

        public NodeWithPopup(string moduleId = "", bool ignorePopupQueue = false)
        {
            _moduleId = moduleId;
            _ignorePopupQueue = ignorePopupQueue;
            CantOpenBefore[moduleId] = DateTime.Now + TimeSpan.FromMilliseconds(200);
        }

        static NodeWithPopup()
        {
            Renderer.OnFrameStart += () => { };
            Renderer.OnFrameEnd += () =>
            {
                if (_pendingOpenedId != _lastOpenedId)
                {
                    CantOpenBefore[_lastOpenedId ?? string.Empty] = DateTime.Now + TimeSpan.FromMilliseconds(200);
                }
                _lastOpenedId = _pendingOpenedId;
                _pendingOpenedId = null;
            };
        }

        public Node Draw(ICollection<Node> module, Func<Node> popup)
        {
            var shouldShowExternal = GetShouldShowPopup(IsHovered);
            var shouldShow = shouldShowExternal && (_lastOpenedId == _moduleId || _ignorePopupQueue);
            if (_ignorePopupQueue == false 
                && shouldShowExternal
                && CantOpenBefore[_moduleId] < DateTime.Now
                && (_lastOpenedId != _moduleId || string.IsNullOrEmpty(_pendingOpenedId)))
            {
                _pendingOpenedId = _moduleId;
            }

            _popupOpacity = PrimitivesMath.LerpSmooth(_popupOpacity, shouldShow ? 1 : 0, 24.0f, DELTA_TIME);

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
                    _popupOpacity > 0.1f
                        ? new BoxNode
                        {
                            IgnoreLayout = true,
                            Opacity = _popupOpacity,
                            HorizontalAlignment = ItemsAlignment.Stretch,
                            Style = new Style { Padding = new Insets(TopOffset, 0, 0, 0) },
                            Children = [popup()],
                        }
                        : new SpacerNode()
                ],
            };
        }
    }

    public const float DELTA_TIME = 1.0f / 60.0f;

    private static readonly AppIconResolver IconResolver = new();

    public static Color ToBackground(Theme theme, Color color) => Color.Lerp(theme.Panel, color, 0.125f) with { A = 0.9f };

    public static Node BuildDivider(Color color, int? width = null, int height = 24) =>
        new BoxNode(width, height)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            Children = [ new BoxNode(height: 2) { Style = new Style { BackgroundColor = color } } ]
        };

    public static Node BuildTextWithIcon(Theme theme, SvgAsset icon, string text, Color? color = null) =>
        new BoxNode
        {
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 8 },
            Children =
            [
                new ImageNode(icon, 18, 18, color ?? theme.Text),
                new TextNode(text, theme.TextSize, color ?? theme.Text),
            ],
        };

    public static Node BuildBadge(string text, float fontSize, Color fill, Theme theme) =>
        new BoxNode
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { BackgroundColor = fill, BorderRadius = new BorderRadius(theme.BorderRadius) },
            Children = { new TextNode(text, fontSize, theme.Text) },
        };

    public static Node BuildAppBadge(string className, int iconSize, Color fill, Theme theme)
    {
        var imagePath = IconResolver.TryResolve(className);
        if (imagePath is null)
        {
            return BuildBadge(AppBadge(className), MathF.Max(8.0f, iconSize - 6.0f), fill, theme);
        }

        return new ImageNode(imagePath, iconSize, iconSize);
    }

    public static Style ModuleStyle(Theme theme, Color fill, bool left = true, bool right = true)
    {
        return new Style
        {
            BackgroundColor = fill,
            BorderColor = theme.Border,
            BorderRadius = new BorderRadius(left ? theme.BorderRadius : 0, right ? theme.BorderRadius : 0,
                right ? theme.BorderRadius : 0, left ? theme.BorderRadius : 0),
            BorderWidth = new Insets(theme.BorderWidth, right ? theme.BorderWidth : 0,
                theme.BorderWidth, left ? theme.BorderWidth : 0),
            Padding = new Insets(8, 6)
        };
    }

    public static Style PopupStyle(Theme theme) => ModuleStyle(theme, Color.FromRgb(0, 0, 0, 0.9f)) with
    {
        BorderRadius = 8,
        Padding = 8,
        Spacing = 8
    };

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
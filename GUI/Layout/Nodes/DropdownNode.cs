using HyprNetShell.GUI.Helpers;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class DropdownNode : Node
{
    private const int TRIGGER_HEIGHT = 36;
    private const int OPTION_HEIGHT = 34;
    private const int TRIGGER_SPACING = 4;
    private const int OPTIONS_SPACING = 8;
    private const int POPUP_PADDING = 8;
    private const float ANIMATION_SPEED = 18.0f;
    private const int MAXIMUM_VISIBLE_OPTIONS = 7;
    private const int WHEEL_OPTIONS = 1;
    private const int SCROLLBAR_WIDTH = 8;
    private const int SCROLLBAR_GAP = 8;

    private readonly IReadOnlyList<string> _options;
    private readonly SvgAsset _chevronIcon;
    private readonly SvgAsset _checkIcon;
    private readonly Action<int> _onSelected;
    private readonly Ref<bool> _triggerHovered = new();
    private readonly Ref<bool>[] _optionHovered;
    private readonly Color[] _optionBackgrounds;
    private Color _triggerBackground;
    private float _chevronRotation;
    private bool _isOpen;
    private bool _alignSelectedOption;
    private int _firstVisibleOption;

    public override int Width
    {
        get;
    }
    public override int Height => TRIGGER_HEIGHT;

    public int SelectedIndex
    {
        get; set;
    }
    public float FontSize { get; init; } = ThemeManager.Current.Text.Size;
    public Color BackgroundColor { get; init; } = Color.FromRgb(31, 35, 44, 0.9f);
    public Color HoverColor { get; init; } = Color.FromRgb(65, 69, 78, 0.95f);
    public Color SelectedColor { get; init; } = Color.Orange;
    public Color BorderColor { get; init; } = ThemeManager.Current.Border.Color;
    public Color TextColor { get; init; } = ThemeManager.Current.Text.Color;
    public Color PopupBackgroundColor { get; init; } = Color.FromRgb(0, 0, 0, 0.85f);
    public float BorderWidth { get; init; } = 1.0f;
    public float BorderRadius { get; init; } = 8.0f;

    public DropdownNode(
        int width,
        IReadOnlyList<string> options,
        int selectedIndex,
        SvgAsset chevronIcon,
        SvgAsset checkIcon,
        Action<int> onSelected)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(onSelected);
        if (options.Count == 0)
        {
            throw new ArgumentException("A dropdown must contain at least one option.", nameof(options));
        }

        if (selectedIndex < 0 || selectedIndex >= options.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(selectedIndex));
        }

        Width = width;
        _options = options;
        _chevronIcon = chevronIcon;
        _checkIcon = checkIcon;
        SelectedIndex = selectedIndex;
        _onSelected = onSelected;
        _optionHovered = Enumerable.Range(0, options.Count).Select(_ => new Ref<bool>()).ToArray();
        _optionBackgrounds = Enumerable.Repeat(BackgroundColor, options.Count).ToArray();
        _triggerBackground = BackgroundColor;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        if (SelectedIndex < 0 || SelectedIndex >= _options.Count)
        {
            throw new InvalidOperationException("SelectedIndex must refer to an existing dropdown option.");
        }

        var inheritedOpacity = Opacity;
        var wasOpen = _isOpen;
        _triggerBackground = AnimateColor(
            _triggerBackground,
            _triggerHovered.Value ? HoverColor : BackgroundColor);

        _chevronRotation = PrimitivesMath.LerpSmooth(
            _chevronRotation,
            _isOpen ? MathF.PI : 0.0f,
            ANIMATION_SPEED,
            Renderer.DeltaTime);

        var root = new BoxNode(Width, TRIGGER_HEIGHT) {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children = [BuildTrigger()],
        };

        root.Opacity = inheritedOpacity;
        root.Draw(renderer, x, y);

        Rect? optionsRect = null;
        if (wasOpen)
        {
            var borderInset = (int)MathF.Ceiling(BorderWidth) * 2;
            var inset = POPUP_PADDING * 2 + borderInset;
            var belowY = Math.Clamp(y + TRIGGER_HEIGHT + TRIGGER_SPACING, 0, renderer.Height);
            var belowHeight = renderer.Height - belowY;
            var aboveHeight = Math.Clamp(y - TRIGGER_SPACING, 0, renderer.Height);
            var desiredCount = Math.Min(MAXIMUM_VISIBLE_OPTIONS, _options.Count);
            var desiredHeight = desiredCount * (OPTION_HEIGHT + OPTIONS_SPACING) - OPTIONS_SPACING + inset;
            var placeAbove = belowHeight < desiredHeight && aboveHeight > belowHeight;
            var availableHeight = placeAbove ? aboveHeight : belowHeight;
            var visibleCount = Math.Min(desiredCount,
                Math.Max(0, (availableHeight - inset + OPTIONS_SPACING) / (OPTION_HEIGHT + OPTIONS_SPACING)));
            var popupWidth = Math.Min(Width + inset, renderer.Width);
            var optionWidth = popupWidth - inset -
                              (_options.Count > visibleCount ? SCROLLBAR_WIDTH + SCROLLBAR_GAP : 0);
            if (visibleCount > 0 && optionWidth >= 42)
            {
                var popupHeight = visibleCount * (OPTION_HEIGHT + OPTIONS_SPACING) - OPTIONS_SPACING + inset;
                var optionsX = Math.Clamp(x, 0, renderer.Width - popupWidth);
                var optionsY = placeAbove ? aboveHeight - popupHeight : belowY;
                var popupRect = new Rect(optionsX, optionsY, popupWidth, popupHeight);
                optionsRect = popupRect;
                var maximumFirstOption = _options.Count - visibleCount;
                if (_alignSelectedOption)
                {
                    _firstVisibleOption = Math.Clamp(SelectedIndex - visibleCount + 1, 0, maximumFirstOption);
                    _alignSelectedOption = false;
                }

                _firstVisibleOption = Math.Clamp(_firstVisibleOption, 0, maximumFirstOption);
                Layout.RegisterLayerInputRegion(RenderLayer.OptionsSelector, popupRect);
                Layout.DrawOnLayer(RenderLayer.OptionsSelector, topRenderer =>
                {
                    var input = Layout.Input;
                    if (!Layout.IsLowerLayerClickBlocked && input.Contains(popupRect) &&
                        float.IsFinite(input.ScrollDelta) && input.ScrollDelta != 0)
                    {
                        // Wayland axis values are distances, not a count of wheel detents.
                        _firstVisibleOption = Math.Clamp(
                            _firstVisibleOption + Math.Sign(input.ScrollDelta) * WHEEL_OPTIONS,
                            0, maximumFirstOption);
                    }

                    var optionsOverlay = BuildOptionsOverlay(popupWidth, popupHeight, optionWidth, visibleCount);
                    optionsOverlay.Opacity = inheritedOpacity;
                    optionsOverlay.Draw(topRenderer, optionsX, optionsY);
                    if (!_isOpen)
                    {
                        Layout.UnregisterNextLayerInputRegion(RenderLayer.OptionsSelector, popupRect);
                    }
                });
            }
        }

        var triggerRect = new Rect(x, y, Width, TRIGGER_HEIGHT);
        if (wasOpen &&
            Layout.Input.PointerPressed &&
            !Layout.Input.Contains(triggerRect) &&
            (optionsRect is null || !Layout.Input.Contains(optionsRect.Value)))
        {
            _isOpen = false;
        }

        SetInteractionState(
            root.LastHovered,
            root.LastHoveredThrough,
            root.LastClicked,
            root.LastClickedThrough);

        // Parent boxes multiply child opacity while traversing the tree. Dropdowns are retained
        // between frames, so restore the local value to avoid cumulative opacity decay.
        Opacity = 1.0f;
    }

    private Node BuildTrigger() => new BoxNode(Width, TRIGGER_HEIGHT) {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        IsHovered = _triggerHovered,
        OnClick = () =>
        {
            _isOpen = !_isOpen;
            _alignSelectedOption = _isOpen;
        },
        Style = ButtonStyle(_triggerBackground) with { Padding = new Insets(12, 7) },
        Children =
        [
            new TextNode(
                _options[SelectedIndex],
                FontSize,
                TextColor,
                Width - 42,
                TextWrapping.Ellipsis),
            new ImageNode(_chevronIcon, 16, 16, TextColor)
            {
                RotationRadians = _chevronRotation,
            },
        ],
    };

    private Node BuildOptionsOverlay(int width, int height, int optionWidth, int visibleCount)
    {
        var contentHeight = visibleCount * (OPTION_HEIGHT + OPTIONS_SPACING) - OPTIONS_SPACING;
        var content = new BoxNode(optionWidth, contentHeight) {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = OPTIONS_SPACING },
            Children = [.. BuildOptions(optionWidth, visibleCount)],
        };
        var children = new List<Node> { content };
        if (_options.Count > visibleCount)
        {
            children.Add(new ScrollbarNode(contentHeight, _firstVisibleOption, _options.Count,
                visibleCount, ThemeManager.Current.Panel, ThemeManager.Current.Text.MutedColor,
                SCROLLBAR_WIDTH));
        }

        return new BoxNode(width, height) {
            IgnoreLayout = true,
            VerticalAlignment = ItemsAlignment.Start,
            Style = new Style {
                BackgroundColor = PopupBackgroundColor,
                BorderColor = BorderColor,
                BorderWidth = BorderWidth,
                BorderRadius = 8,
                Padding = POPUP_PADDING,
                Spacing = SCROLLBAR_GAP,
                ShadowColor = Color.Black with { A = 0.65f },
                ShadowDistance = 8.0f,
            },
            Children = children,
        };
    }

    private IEnumerable<Node> BuildOptions(int width, int visibleCount)
    {
        for (var index = _firstVisibleOption; index < _firstVisibleOption + visibleCount; index++)
        {
            var optionIndex = index;
            var target = _optionHovered[index].Value
                ? HoverColor
                : index == SelectedIndex ? SelectedColor : BackgroundColor;
            _optionBackgrounds[index] = AnimateColor(_optionBackgrounds[index], target);

            yield return new BoxNode(width, OPTION_HEIGHT) {
                HorizontalAlignment = ItemsAlignment.Spread,
                VerticalAlignment = ItemsAlignment.Center,
                IsHovered = _optionHovered[index],
                OnClick = () => Select(optionIndex),
                Style = OptionStyle(_optionBackgrounds[index], index == SelectedIndex),
                Children =
                [
                    new TextNode(
                        _options[index],
                        FontSize,
                        TextColor,
                        width - 42,
                        TextWrapping.Ellipsis),
                    index == SelectedIndex
                        ? new ImageNode(_checkIcon, 16, 16, TextColor)
                        : new SpacerNode(16, 16),
                ],
            };
        }
    }

    private void Select(int index)
    {
        SelectedIndex = index;
        _isOpen = false;
        _onSelected(index);
    }

    private Style ButtonStyle(Color background) => new() {
        BackgroundColor = background with {
            A = 1.0f
        },
        BorderColor = BorderColor,
        BorderWidth = BorderWidth,
        BorderRadius = BorderRadius,
    };

    private Style OptionStyle(Color background, bool selected) => new() {
        BackgroundColor = background with {
            A = 1.0f
        },
        BorderColor = BorderColor,
        BorderWidth = selected ? BorderWidth : 0,
        BorderRadius = BorderRadius,
        Padding = new Insets(12, 6),
    };

    private static Color AnimateColor(Color current, Color target) =>
        Color.LerpSmooth(current, target, ANIMATION_SPEED, Renderer.DeltaTime);
}

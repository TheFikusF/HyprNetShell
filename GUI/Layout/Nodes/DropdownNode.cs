using HyprNetShell.GUI.Helpers;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public sealed class DropdownNode : Node
{
    private const int TriggerHeight = 36;
    private const int OptionHeight = 34;
    private const int TriggerSpacing = 4;
    private const int OptionsSpacing = 8;
    private const int PopupPadding = 8;
    private const float AnimationSpeed = 18.0f;

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

    public override int Width { get; }
    public override int Height => TriggerHeight;

    public int SelectedIndex { get; set; }
    public float FontSize { get; init; } = 14.0f;
    public Color BackgroundColor { get; init; } = Color.FromRgb(31, 35, 44, 0.9f);
    public Color HoverColor { get; init; } = Color.FromRgb(65, 69, 78, 0.95f);
    public Color SelectedColor { get; init; } = Color.Orange;
    public Color BorderColor { get; init; } = Color.White;
    public Color TextColor { get; init; } = Color.White;
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
            AnimationSpeed,
            Renderer.DeltaTime);

        var root = new BoxNode(Width, TriggerHeight)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children = [BuildTrigger()],
        };

        root.Opacity = inheritedOpacity;
        root.Draw(renderer, x, y);

        Rect? optionsRect = null;
        if (wasOpen)
        {
            var optionsY = y + TriggerHeight + TriggerSpacing;
            var optionsOverlay = BuildOptionsOverlay();
            optionsRect = new Rect(x, optionsY, optionsOverlay.Width, optionsOverlay.Height);
            Layout.RegisterTopLayerInputRegion(optionsRect.Value);
            optionsOverlay.Opacity = inheritedOpacity;
            Layout.DrawOnTop(topRenderer =>
            {
                optionsOverlay.Draw(topRenderer, x, optionsY);
                if (!_isOpen)
                {
                    Layout.UnregisterNextTopLayerInputRegion(optionsRect.Value);
                }
            });
        }

        var triggerRect = new Rect(x, y, Width, TriggerHeight);
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

    private Node BuildTrigger() => new BoxNode(Width, TriggerHeight)
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        IsHovered = _triggerHovered,
        OnClick = () => _isOpen = !_isOpen,
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

    private Node BuildOptionsOverlay()
    {
        var borderInset = (int)MathF.Ceiling(BorderWidth * 2.0f);
        var width = Width + PopupPadding * 2 + borderInset;
        var height = OptionHeight * _options.Count +
                     OptionsSpacing * Math.Max(0, _options.Count - 1) +
                     PopupPadding * 2 +
                     borderInset;
        return new BoxNode(width, height)
        {
            IgnoreLayout = true,
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style
            {
                BackgroundColor = PopupBackgroundColor,
                BorderColor = BorderColor,
                BorderWidth = BorderWidth,
                BorderRadius = 8,
                Padding = PopupPadding,
                Spacing = OptionsSpacing,
                ShadowColor = Color.Black with { A = 0.65f },
                ShadowDistance = 8.0f,
            },
            Children = [..BuildOptions()],
        };
    }

    private IEnumerable<Node> BuildOptions()
    {
        for (var index = 0; index < _options.Count; index++)
        {
            var optionIndex = index;
            var target = _optionHovered[index].Value
                ? HoverColor
                : index == SelectedIndex ? SelectedColor : BackgroundColor;
            _optionBackgrounds[index] = AnimateColor(_optionBackgrounds[index], target);

            yield return new BoxNode(Width, OptionHeight)
            {
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
                        Width - 42,
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

    private Style ButtonStyle(Color background) => new()
    {
        BackgroundColor = background with { A = 1.0f },
        BorderColor = BorderColor,
        BorderWidth = BorderWidth,
        BorderRadius = BorderRadius,
    };

    private Style OptionStyle(Color background, bool selected) => new()
    {
        BackgroundColor = background with { A = 1.0f },
        BorderColor = BorderColor,
        BorderWidth = selected ? BorderWidth : 0,
        BorderRadius = BorderRadius,
        Padding = new Insets(12, 6),
    };

    private static Color AnimateColor(Color current, Color target) =>
        Color.LerpSmooth(current, target, AnimationSpeed, Renderer.DeltaTime);
}

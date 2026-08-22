using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Common;

public class NodeWithPopup
{
    private readonly PopupCoordinator _popupCoordinator;
    private readonly string _moduleId;
    private readonly bool _ignorePopupQueue;

    private float _popupOpacity = 0f;

    public int TopOffset { get; init; } = 33;
    public ItemsAlignment HorizontalAlignment { get; init; }
    public Func<bool, bool> GetShouldShowPopup { get; init; } = hovered => hovered;

    private readonly Ref<bool> _hovered = new();

    public bool IsHovered => _hovered.Value;
    public bool ShouldShowPopup => GetShouldShowPopup(IsHovered);

    public NodeWithPopup(
        PopupCoordinator popupCoordinator,
        string moduleId = "",
        bool ignorePopupQueue = false)
    {
        _popupCoordinator = popupCoordinator;
        _moduleId = moduleId;
        _ignorePopupQueue = ignorePopupQueue;
        _popupCoordinator.Register(moduleId);
    }

    public Node Draw(ICollection<Node> module, Func<Node> popup)
    {
        var shouldShowExternal = GetShouldShowPopup(IsHovered);
        var shouldShow = shouldShowExternal && (_popupCoordinator.IsOpen(_moduleId) || _ignorePopupQueue);
        if (!_ignorePopupQueue && shouldShowExternal)
        {
            _popupCoordinator.TryRequestOpen(_moduleId);
        }

        _popupOpacity = PrimitivesMath.LerpSmooth(_popupOpacity, shouldShow ? 1 : 0, 24.0f, ModulesCommon.DELTA_TIME);

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

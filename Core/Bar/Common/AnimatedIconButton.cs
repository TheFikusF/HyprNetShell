using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Rendering.Renderers;

namespace HyprNetShell.Core.Bar.Common;

internal sealed class AnimatedIconButton : ModulesCommon.BoxState
{
    private const int SIZE = 32;
    private const int ICON_SIZE = 18;
    private float _expansion;

    public Node Build(SvgAsset icon, bool visible, Action onClick, bool enabled = true)
    {
        var target = visible ? 1.0f : 0.0f;
        _expansion = PrimitivesMath.LerpSmooth(_expansion, target, 18.0f, Renderer.DeltaTime);
        if (MathF.Abs(_expansion - target) < 0.001f)
        {
            _expansion = target;
        }

        Background = Color.LerpSmooth(Background,
            Color.White with { A = visible && enabled && Hovered.Value ? 0.3f : 0.0f },
            18.0f, Renderer.DeltaTime);
        var width = (int)MathF.Round(SIZE * _expansion);
        if (width == 0)
        {
            Hovered.Value = false;
            return new SpacerNode();
        }

        return new BoxNode(width, SIZE) {
            Opacity = _expansion,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = visible && enabled ? onClick : null,
            IsHovered = Hovered,
            Style = ModulesCommon.ModuleStyle(Background) with {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
                ShadowColor = null,
            },
            Children = width >= ICON_SIZE
                ? [new ImageNode(icon, ICON_SIZE, ICON_SIZE, enabled ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor)]
                : [],
        };
    }
}

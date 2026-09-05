using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class PrivacyModule(
    PrivacyModuleService service,
    Theme theme,
    PopupCoordinator popupCoordinator) : IDrawableModule
{
    private const long ICON_INTERVAL_MS = 3000;
    private const float ICON_FADE_DECAY = 8.0f;

    private readonly NodeWithPopup _node = new(popupCoordinator, "privacy_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    private SvgAsset? _currentIcon;
    private SvgAsset? _previousIcon;

    private float _iconOpacity = 1.0f;
    private float _widgetScale = 0.0f;

    public Node Draw()
    {
        var privacy = service.Snapshot;
        _widgetScale = PrimitivesMath.LerpSmooth(_widgetScale, privacy.IsActive ? 1 : 0, 18.0f, Renderer.DeltaTime);
        if (_widgetScale < 0.05f)
        {
            _widgetScale = 0f;
            _currentIcon = null;
            _previousIcon = null;
            _iconOpacity = 1.0f;
            return new SpacerNode();
        }

        return _node.Draw([BuildStateModule(privacy)], privacy.IsActive
            ? () => BuildPopup(privacy)
            : () => new SpacerNode());
    }

    private BoxNode BuildStateModule(PrivacySnapshot privacy)
    {
        var icons = ActiveIcons(privacy);
        var desiredIcon = icons.Count != 0
            ? icons[(int)(Environment.TickCount64 / ICON_INTERVAL_MS % icons.Count)]
            : null;

        if (_currentIcon is null)
        {
            _currentIcon = desiredIcon;
        }
        else if (!ReferenceEquals(desiredIcon, _currentIcon))
        {
            _previousIcon = _currentIcon;
            _currentIcon = desiredIcon;
            _iconOpacity = 0.0f;
        }

        _iconOpacity = PrimitivesMath.LerpSmooth(_iconOpacity, 1.0f, ICON_FADE_DECAY, Renderer.DeltaTime);

        if (_iconOpacity > 0.995f)
        {
            _iconOpacity = 1.0f;
            _previousIcon = null;
        }

        int size = (int)(_widgetScale * 36.0f);

        return new BoxNode(size, size)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style
            {
                BackgroundColor = Color.Orange,
                BorderRadius = 999,
                ShadowColor = Color.Black with { A = 0.45f },
                ShadowDistance = 4,
            },
            Children = [BuildAnimatedIcon()],
        };
    }

    private BoxNode BuildAnimatedIcon()
    {
        int size = (int)(_widgetScale * 18.0f);
        var children = new List<Node>(2);
        if (_previousIcon is not null)
        {
            children.Add(new BoxNode
            {
                IgnoreLayout = true,
                Opacity = 1.0f - _iconOpacity,
                Children = [new ImageNode(_previousIcon, size, size, theme.Panel)],
            });
        }

        children.Add(new BoxNode
        {
            IgnoreLayout = true,
            Opacity = _iconOpacity,
            Children = [new ImageNode(_currentIcon!, size, size, theme.Panel)],
        });

        return new BoxNode(size, size)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Children = children,
        };
    }

    private BoxNode BuildPopup(PrivacySnapshot privacy) => new(340)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.PopupStyle(theme) with { Spacing = 8 },
        Children =
        [
            new TextNode("Privacy", 18, theme.Text),
            ..BuildUsageRows(Icons.ScreenShare, "Screen recording", privacy.ScreenRecordingApplications),
            ..BuildUsageRows(Icons.Microphone, "Microphone", privacy.MicrophoneApplications),
            ..BuildUsageRows(Icons.Camera, "Camera", privacy.CameraApplications),
        ],
    };

    private IEnumerable<Node> BuildUsageRows(SvgAsset icon, string usage, IReadOnlyList<string> applications)
    {
        foreach (var application in applications)
        {
            yield return new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Center,
                Style = ModulesCommon.ModuleStyle(theme, ModulesCommon.ToBackground(theme, Color.Orange)) with
                {
                    BorderRadius = 8,
                    ShadowColor = null,
                    Spacing = 10,
                },
                Children =
                [
                    new ImageNode(icon, 18, 18, theme.Text),
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        Style = new Style { Spacing = 2 },
                        Children =
                        [
                            new TextNode(usage, 13, theme.Text.MutedColor),
                            new TextNode(application, 15, theme.Text),
                        ],
                    },
                ],
            };
        }
    }

    private static IReadOnlyList<SvgAsset> ActiveIcons(PrivacySnapshot privacy)
    {
        var icons = new List<SvgAsset>(3);
        if (privacy.IsScreenRecording)
        {
            icons.Add(Icons.ScreenShare);
        }

        if (privacy.IsMicrophoneInUse)
        {
            icons.Add(Icons.Microphone);
        }

        if (privacy.IsCameraInUse)
        {
            icons.Add(Icons.Camera);
        }

        return icons;
    }
}

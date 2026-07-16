using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal static class NotificationCard
{
    private static readonly AppIconResolver IconResolver = new();

    public static Node Draw(
        NotificationSnapshot notification,
        NotificationService service,
        Theme theme)
    {
        var iconPath = string.IsNullOrWhiteSpace(notification.IconName)
            ? null
            : IconResolver.TryResolveIcon(notification.IconName) ?? IconResolver.TryResolve(notification.IconName);

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.PopupStyle(theme) with
            {
                BorderRadius = 12,
                Padding = 8,
                Spacing = 12,
            },
            Children =
            [
                new BoxNode
                {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Start,
                    Style = new Style { Spacing = 12 },
                    Children =
                    [
                        BuildContent(notification, iconPath, service, theme),
                        BuildCloseButton(notification.Id, service, theme),
                    ],
                },
                ..BuildActions(notification, service, theme),
            ],
        };
    }

    private static Node BuildContent(
        NotificationSnapshot notification,
        string? iconPath,
        NotificationService service,
        Theme theme) =>
        new BoxNode
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            OnClick = () => service.Activate(notification.Id),
            Style = new Style { Spacing = 12 },
            Children =
            [
                ..BuildIcon(notification.ImageData, iconPath, 32),
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Start,
                    Style = new Style { Spacing = 4 },
                    Children =
                    [
                        new TextNode(notification.Title, 16, theme.Text, wrapping: TextWrapping.Ellipsis),
                        new TextNode(
                            notification.Body,
                            theme.TextSize,
                            theme.Text,
                            wrapping: TextWrapping.Wrap,
                            maxLines: 3),
                        ..(!string.IsNullOrWhiteSpace(notification.AppName)
                            ? (List<TextNode>)[
                                new TextNode(
                                    notification.AppName,
                                    11,
                                    theme.Muted,
                                    wrapping: TextWrapping.Ellipsis),
                            ]
                            : []),
                    ],
                },
            ],
        };

    private static Node BuildCloseButton(uint id, NotificationService service, Theme theme) =>
        new BoxNode(22, 22)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => service.Dismiss(id),
            Style = new Style
            {
                BackgroundColor = theme.Muted,
                BorderRadius = 6,
                Padding = 4,
            },
            Children = [new ImageNode(Icons.X, 14, 14, theme.Text)],
        };

    private static IEnumerable<Node> BuildActions(
        NotificationSnapshot notification,
        NotificationService service,
        Theme theme)
    {
        var actions = notification.Actions
            .Where(action => action.Key != "default")
            .ToArray();
        if (actions.Length == 0)
        {
            yield break;
        }

        yield return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 6 },
            Children =
            [
                ..actions.Select(action => new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Center,
                    OnClick = () => service.InvokeAction(notification.Id, action.Key),
                    Style = new Style
                    {
                        BackgroundColor = theme.Active,
                        BorderRadius = 6,
                        Padding = 8,
                    },
                    Children =
                    [
                        new TextNode(
                            action.Label,
                            12,
                            theme.Text,
                            wrapping: TextWrapping.Ellipsis),
                    ],
                }),
            ],
        };
    }

    private static IEnumerable<Node> BuildIcon(RawImageData? imageData, string? iconPath, int size)
    {
        if (imageData is not null)
        {
            yield return new ImageNode(imageData, size, size);
        }
        else if (!string.IsNullOrWhiteSpace(iconPath))
        {
            yield return new ImageNode(iconPath, size, size);
        }
    }

}

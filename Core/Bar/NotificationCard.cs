using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal static class NotificationCard
{
    internal sealed class State
    {
        public ModulesCommon.BoxState Content { get; } = new();
        public ModulesCommon.BoxState CloseButton { get; } = new();
        public Dictionary<string, ModulesCommon.BoxState> ActionButtons { get; } = new();
        public bool ContentInitialized { get; set; }
        public bool CloseButtonInitialized { get; set; }
    }

    private static readonly AppIconResolver IconResolver = new();

    public static Node Draw(
        NotificationSnapshot notification,
        NotificationService service,
        Theme theme,
        State state)
    {
        var svgIcon = Icons.ByName.GetValueOrDefault(notification.IconName);
        var iconPath = svgIcon is null && !string.IsNullOrWhiteSpace(notification.IconName)
            ? IconResolver.TryResolveIcon(notification.IconName) ?? IconResolver.TryResolve(notification.IconName)
            : null;

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
                        BuildContent(notification, svgIcon, iconPath, service, theme, state),
                        BuildCloseButton(notification.Id, service, theme, state),
                    ],
                },
                ..BuildActions(notification, service, theme, state),
            ],
        };
    }

    private static Node BuildContent(
        NotificationSnapshot notification,
        SvgAsset? svgIcon,
        string? iconPath,
        NotificationService service,
        Theme theme,
        State state)
    {
        if (!state.ContentInitialized)
        {
            state.Content.Background = theme.Panel with { A = 0.2f };
            state.ContentInitialized = true;
        }
        state.Content.UpdateColor(theme.Panel with { A = 0.2f });

        TextNode[] children = string.IsNullOrWhiteSpace(notification.Body)
            ? [new TextNode(notification.Title, theme.Text, theme.Text, wrapping: TextWrapping.Wrap, maxLines: 3)]
            :
            [
                new TextNode(notification.Title, 16, theme.Text, wrapping: TextWrapping.Ellipsis),
                new TextNode(
                    notification.Body,
                    theme.Text,
                    theme.Text,
                    wrapping: TextWrapping.Wrap,
                    maxLines: 3),
            ];
        return new BoxNode
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Start,
            OnClick = () => service.Activate(notification.Id),
            IsHovered = state.Content.Hovered,
            Style = new Style
            {
                BackgroundColor = state.Content.Background,
                BorderRadius = 8,
                Padding = 4,
                Spacing = 12,
            },
            Children =
            [
                ..BuildIcon(notification, svgIcon, iconPath, theme),
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Start,
                    Style = new Style { Spacing = 4 },
                    Children =
                    [
                        ..children,
                        ..(!string.IsNullOrWhiteSpace(notification.AppName)
                            ? (List<TextNode>)
                            [
                                new TextNode(
                                    notification.AppName,
                                    11,
                                    theme.Text.MutedColor,
                                    wrapping: TextWrapping.Ellipsis),
                            ]
                            : []),
                    ],
                },
            ],
        };
    }

    private static Node BuildCloseButton(
        uint id,
        NotificationService service,
        Theme theme,
        State state)
    {
        if (!state.CloseButtonInitialized)
        {
            state.CloseButton.Background = theme.Text.MutedColor;
            state.CloseButtonInitialized = true;
        }
        state.CloseButton.UpdateColor(theme.Text.MutedColor);
        return new BoxNode(22, 22)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => service.Dismiss(id),
            IsHovered = state.CloseButton.Hovered,
            Style = new Style
            {
                BackgroundColor = state.CloseButton.Background,
                BorderRadius = 6,
                Padding = 4,
            },
            Children = [new ImageNode(Icons.X, 14, 14, theme.Text)],
        };
    }

    private static IEnumerable<Node> BuildActions(
        NotificationSnapshot notification,
        NotificationService service,
        Theme theme,
        State state)
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
                ..actions.Select(action => BuildAction(notification.Id, action, service, theme, state)),
            ],
        };
    }

    private static Node BuildAction(
        uint notificationId,
        NotificationActionSnapshot action,
        NotificationService service,
        Theme theme,
        State state)
    {
        var buttonState = state.ActionButtons
            .GetState(action.Key, theme.Active)
            .UpdateColor(theme.Active);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Center,
            OnClick = () => service.InvokeAction(notificationId, action.Key),
            IsHovered = buttonState.Hovered,
            Style = new Style
            {
                BackgroundColor = buttonState.Background,
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
        };
    }

    private static IEnumerable<Node> BuildIcon(
        NotificationSnapshot notification,
        SvgAsset? svgIcon,
        string? iconPath,
        Theme theme)
    {
        var width = notification.ShowImageAsPreview ? 128 : 32;
        var height = notification.ShowImageAsPreview ? 80 : 32;
        if (notification.ImageData is not null)
        {
            yield return new ImageNode(notification.ImageData, width, height);
        }
        else if (notification.StoredImage is not null)
        {
            yield return new ImageNode(notification.StoredImage, width, height);
        }
        else if (svgIcon is not null)
        {
            yield return new ImageNode(svgIcon, 32, 32, theme.Text);
        }
        else if (!string.IsNullOrWhiteSpace(iconPath))
        {
            yield return new ImageNode(iconPath, 32, 32);
        }
    }
}
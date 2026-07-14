using System.Diagnostics;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class NotificationsWidget(Theme theme)
{
    private readonly AppIconResolver _iconResolver = new();

    public Node Draw(NotificationsSnapshot snapshot) => new BoxNode(596)
    {
        Direction = Direction.Vertical,
        VerticalAlignment = ItemsAlignment.Start,
        Style = new Style { Spacing = 8 },
        Children =
        [
            new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Center,
                Style = new Style { Spacing = 8 },
                Children =
                [
                    new ImageNode(Icons.Bell, 22, 22, theme.Text),
                    new TextNode("Notifications", 22, theme.Text)
                ]
            },
            ..BuildRows(snapshot),
        ],
    };

    public static void OpenPanel()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "swaync-client",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-op" },
            });
        }
        catch
        {
            // swaync is optional.
        }
    }

    private IEnumerable<Node> BuildRows(NotificationsSnapshot snapshot)
    {
        if (snapshot.Items.Count == 0)
        {
            yield return snapshot.Count == 0
                ? new TextNode("No notifications", 14, theme.Muted)
                : new BoxNode
                {
                    OnClick = OpenPanel,
                    Style = new Style
                    {
                        BackgroundColor = theme.Muted,
                        BorderRadius = 8,
                        Padding = new Insets(8, 6),
                    },
                    Children =
                    [
                        new TextNode($"Open swaync notification center ({snapshot.Count})", 14, theme.Text),
                    ],
                };
            yield break;
        }

        foreach (var notification in snapshot.Items.Take(5))
        {
            var iconPath = string.IsNullOrWhiteSpace(notification.IconName)
                ? null
                : _iconResolver.TryResolve(notification.IconName);
            yield return new BoxNode
            {
                VerticalAlignment = ItemsAlignment.Center,
                Style = new Style
                {
                    BackgroundColor = theme.Panel,
                    BorderRadius = 8,
                    Padding = new Insets(8, 6),
                    BorderWidth = 2,
                    BorderColor = theme.Border,
                    Spacing = 8,
                },
                Children =
                [
                    ..BuildIcon(iconPath),
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        VerticalAlignment = ItemsAlignment.Start,
                        Style = new Style { Spacing = 4 },
                        Children =
                        [
                            new TextNode(Trim(notification.Title, 52), 14, theme.Text),
                            new TextNode(Trim(notification.Body, 64), 13, theme.Text),
                        ],
                    },
                ],
            };
        }
    }

    private static IEnumerable<Node> BuildIcon(string? iconPath)
    {
        if (!string.IsNullOrWhiteSpace(iconPath))
        {
            yield return new ImageNode(iconPath, 28, 28);
        }
    }

    private static string Trim(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 3)] + "...";
}

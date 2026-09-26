using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.OnlineAccounts;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class ChatGptLimitsWidget(ChatGptUsageService usage, Theme theme)
{
    public const int WIDTH = CenterModule.WIDTH - TodaysEventsWidget.WIDTH - 12;
    private readonly ModulesCommon.BoxState _titleState = new();

    internal Node Draw() => new BoxNode(WIDTH)
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        VerticalAlignment = ItemsAlignment.Start,
        Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
        {
            BorderRadius = 8,
            Spacing = 16,
        },
        Children =
        [
            BuildHeader(usage.Snapshot),
            ..BuildContent(usage.Snapshot),
        ],
    };

    private BoxNode BuildHeader(ChatGptUsageSnapshot snapshot) => new (Style.Spacer, ItemsAlignment.Stretch)
    {
        Direction = Direction.Vertical,
        Children = [
            ModulesCommon.CentralWidgetHeader(Icons.Bot, "ChatGPT limits", null, _titleState, theme),
            new BoxNode(Style.Spacer, ItemsAlignment.Spread)
            {
                new TextNode(snapshot.Plan?.ToUpperInvariant() ?? "", theme.Text, theme.Text.MutedColor),
                new TextNode(
                    snapshot.Status ?? (snapshot.UpdatedAt is { } updated ? $"Updated {updated:t}" : ""),
                    theme.Text,
                    theme.Text.MutedColor),
            }
        ]
    };

    private IEnumerable<Node> BuildContent(ChatGptUsageSnapshot snapshot)
    {
        if (!snapshot.Connected || snapshot.Windows.Count == 0)
        {
            yield return new BoxNode(height: 80)
            {
                HorizontalAlignment = ItemsAlignment.Center,
                VerticalAlignment = ItemsAlignment.Center,
                Children =
                [
                    new TextNode(
                        snapshot.Status ?? "Limits unavailable",
                        theme.Text,
                        theme.Text.MutedColor,
                        WIDTH - 32,
                        TextWrapping.Wrap),
                ],
            };
            yield break;
        }

        foreach (var window in snapshot.Windows)
        {
            yield return BuildWindow(window);
        }
    }

    private BoxNode BuildWindow(ChatGptLimitWindow window)
    {
        const int BarWidth = WIDTH - 24;
        var used = Math.Clamp(window.UsedPercent, 0, 100);
        var color = used >= 90 ? theme.Critical : used >= 70 ? theme.Warning : theme.Active;
        var reset = window.ResetsAt is { } resetsAt ? $"Resets {FormatReset(resetsAt)}" : "Reset unknown";
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = Style.Spacer,
            Children =
            [
                new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
                {
                    new TextNode(window.Label, theme.Text, theme.Text),
                    new TextNode($"{used:0}% used", theme.Text, color),
                },
                new BoxNode(BarWidth, 8)
                {
                    Style = new Style
                    {
                        BackgroundColor = Color.Lighten(theme.Panel, 0.16f),
                        BorderRadius = 999,
                    },
                    Children =
                    [
                        new BoxNode(Math.Max(1, (int)Math.Round(BarWidth * used / 100)), 8)
                        {
                            Style = new Style { BackgroundColor = color, BorderRadius = 999 },
                        },
                    ],
                },
                new TextNode(reset, theme.Text.SmallSize, theme.Text.MutedColor),
            ],
        };
    }

    private static string FormatReset(DateTimeOffset reset)
    {
        var remaining = reset - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "soon";
        }

        return remaining.TotalDays >= 1
            ? reset.ToString("ddd HH:mm")
            : remaining.TotalHours >= 1
                ? $"in {Math.Ceiling(remaining.TotalHours):0}h"
                : $"in {Math.Max(1, Math.Ceiling(remaining.TotalMinutes)):0}m";
    }
}

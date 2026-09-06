using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.LockScreen;

public enum LockScreenStatus
{
    Ready,
    Authenticating,
    Denied,
    Error,
}

public sealed class LockScreenView(Theme theme)
{
    public Node Build(
        int width,
        int height,
        int passwordLength,
        LockScreenStatus status,
        RawImageData? background)
    {
        var bullets = new string('•', Math.Clamp(passwordLength, 0, 64));
        var statusText = status switch
        {
            LockScreenStatus.Authenticating => "Checking…",
            LockScreenStatus.Denied => "Authentication failed",
            LockScreenStatus.Error => "Authentication unavailable",
            _ => "Enter your password to unlock",
        };

        var statusColor = status is LockScreenStatus.Denied or LockScreenStatus.Error
            ? theme.Critical
            : theme.Text.MutedColor;

        var now = DateTime.Now;

        var panel = new BoxNode(440)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 32 },
            Children = [
                new BoxNode(Style.Spacer, ItemsAlignment.Center, ItemsAlignment.Center)
                {
                    Direction = Direction.Vertical,
                    Children = [
                        new TextNode(now.ToString("HH:mm"), 64, theme.Text)
                        {
                            ShadowDistance = 2,
                            ShadowColor = Color.Black,
                        },
                        new TextNode(now.ToString("dddd dd, MMM"), 18, theme.Text)
                        {
                            ShadowDistance = 2,
                            ShadowColor = Color.Black,
                        }
                    ]
                },
                new BoxNode(440)
                {
                    Direction = Direction.Vertical,
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = ModulesCommon.PopupStyle(theme) with
                    {
                        BorderColor = theme.Border.Color,
                        BorderRadius = 16,
                        Padding = new Insets(34),
                        Spacing = 18,
                        ShadowDistance = 14,
                        ShadowColor = Color.Black with { A = 0.5f }
                    },
                    Children =
                    [
                        new TextNode(Environment.UserName, theme.Text.HeaderSize, theme.Text.MutedColor),
                        new BoxNode(360, 52)
                        {
                            HorizontalAlignment = ItemsAlignment.Center,
                            VerticalAlignment = ItemsAlignment.Center,
                            Style = ModulesCommon.ModuleStyle(theme, Color.Black with { A = 0.48f }) with
                            {
                                BorderColor = status is LockScreenStatus.Denied or LockScreenStatus.Error
                                    ? theme.Critical
                                    : theme.Border,
                                BorderRadius = 10,
                                Padding = new Insets(12, 8),
                                ShadowColor = null,
                            },
                            Children =
                            [
                                new TextNode(
                                    bullets.Length == 0 ? "Password" : bullets,
                                    20,
                                    bullets.Length == 0 ? theme.Text.MutedColor : theme.Text)
                            ],
                        },
                        new TextNode(statusText, theme.Text, statusColor),
                    ],
                }            ]
        };

        return new LockScreenRootNode(width, height, background, panel, theme.Panel with { A = 1 });
    }

    private sealed class LockScreenRootNode(
        int width,
        int height,
        RawImageData? background,
        Node content,
        Color fallback) : Node
    {
        public override int Width { get; } = width;
        public override int Height { get; } = height;

        public override void Draw(IRenderApi renderer, int x, int y)
        {
            var bounds = new Rect(x, y, Width, Height);
            if (background is null)
            {
                renderer.FillRect(bounds, fallback);
            }
            else
            {
                renderer.DrawImage(background, bounds, Color.White);
            }

            renderer.FillRect(bounds, Color.Black with { A = 0.28f });
            content.Draw(
                renderer,
                x + Math.Max(0, (Width - content.Width) / 2),
                y + Math.Max(0, (Height - content.Height) / 2));
            SetInteractionState(false, false, false, false);
        }
    }
}

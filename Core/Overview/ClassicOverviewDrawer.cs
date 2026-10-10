using HyprNetShell.Core.Assets;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Overview;

internal sealed class ClassicOverviewDrawer : OverviewDrawer
{
    private const float THUMBNAILS_SCALE = 0.88f;
    private const float THUMBNAILS_HORIZONTAL_PADDING = 56f;
    private const float THUMBNAILS_TOP_PADDING = 160f;
    private const float THUMBNAILS_BOTTOM_PADDING = 80f;
    private const float THUMBNAILS_GAP = 24f;

    public override Point ProjectCenter(Rect bounds, int width, int height) =>
        new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);

    public override Rect[] BuildLayout(int width, int height, float[] ratios)
    {
        var result = new Rect[ratios.Length];
        if (ratios.Length == 0)
        {
            return result;
        }

        var availableWidth = Math.Max(1, width - 2 * THUMBNAILS_HORIZONTAL_PADDING);
        var availableHeight = Math.Max(1, height - THUMBNAILS_TOP_PADDING - THUMBNAILS_BOTTOM_PADDING);

        List<List<int>> bestRows = [];
        float bestHeight = 0;
        // Try balanced contiguous rows, then choose the largest common preview height that fits both axes.
        for (var count = 1; count <= ratios.Length; count++)
        {
            var rows = Enumerable.Range(0, count).Select(_ => new List<int>()).ToList();
            var remaining = ratios.Sum();
            var index = 0;
            for (var row = 0; row < count; row++)
            {
                var target = remaining / (count - row);
                float sum = 0;
                while (index < ratios.Length - (count - row - 1))
                {
                    if (rows[row].Count > 0 && row < count - 1 && MathF.Abs(sum - target) < MathF.Abs(sum + ratios[index] - target))
                    {
                        break;
                    }

                    rows[row].Add(index);
                    sum += ratios[index++];
                }

                remaining -= sum;
            }

            var h = Math.Min(340, (availableHeight - (count - 1) * THUMBNAILS_GAP - count * THUMBNAIL_CAPTION_HEIGHT) / count);
            foreach (var row in rows)
            {
                h = Math.Min(h, (availableWidth - (row.Count - 1) * THUMBNAILS_GAP) / row.Sum(i => ratios[i]));
            }

            if (h <= bestHeight)
            {
                continue;
            }

            bestHeight = h;
            bestRows = rows;
        }

        if (bestRows.Count == 0)
        {
            return result;
        }

        var y = THUMBNAILS_TOP_PADDING + (availableHeight - bestRows.Count * (bestHeight + THUMBNAIL_CAPTION_HEIGHT) - (bestRows.Count - 1) * THUMBNAILS_GAP) / 2;
        foreach (var row in bestRows)
        {
            var rowWidth = row.Sum(i => ratios[i] * bestHeight) + (row.Count - 1) * THUMBNAILS_GAP;
            var x = (width - rowWidth) / 2;
            foreach (var i in row)
            {
                var w = ratios[i] * bestHeight;
                result[i] = new Rect(x, y, w, bestHeight);
                x += w + THUMBNAILS_GAP;
            }

            y += bestHeight + THUMBNAIL_CAPTION_HEIGHT + THUMBNAILS_GAP;
        }

        return result;
    }

    public override string? Draw(IRenderApi renderer, OverviewDrawContext context)
    {
        DrawBackground(renderer, context);
        var (width, height, x, y) = (context.Width, context.Height, context.X, context.Y);
        var progress = context.Progress;
        var dt = context.DeltaTime;
        var pointerOverSearch = context.PointerOverOverlay;
        var theme = ThemeManager.Current;
        string? activated = null;
        foreach (var preview in context.Previews)
        {
            var bounds = preview.Bounds;
            var hover = Hover.GetValueOrDefault(preview.Address);
            Rect GetBounds(float amount)
            {
                var animatedScale = THUMBNAILS_SCALE * (0.94f + 0.06f * progress + 0.035f * amount);
                return new Rect(x + bounds.X + bounds.Width * (1 - animatedScale) / 2,
                    y + bounds.Y + bounds.Height * (1 - animatedScale) / 2 + 18 * (1 - progress) - 7 * amount,
                    bounds.Width * animatedScale, bounds.Height * animatedScale);
            }

            var previousRect = GetBounds(hover);
            var hit = new Rect(previousRect.X, previousRect.Y, previousRect.Width, previousRect.Height + THUMBNAIL_CAPTION_HEIGHT);
            var hovered = context.Interactive && !pointerOverSearch && Layout.Input.HasPointer && hit.Contains(Layout.Input.PointerX, Layout.Input.PointerY);
            hover += ((hovered ? 1 : 0) - hover) * (float)(1 - Math.Exp(-dt / 0.07));
            Hover[preview.Address] = hover;
            var scale = THUMBNAILS_SCALE * (0.94f + 0.06f * progress + 0.035f * hover);
            var rect = GetBounds(hover);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            renderer.FillRoundedShadow(rect, 6, Color.FromRgb(0, 0, 0, 0.35f * progress), 12);
            if (preview.Frame is { } frame)
            {
                var fit = Math.Min(rect.Width / frame.Image.Width, rect.Height / frame.Image.Height);
                var image = new Rect(rect.X + (rect.Width - frame.Image.Width * fit) / 2,
                    rect.Y + (rect.Height - frame.Image.Height * fit) / 2, frame.Image.Width * fit, frame.Image.Height * fit);
                renderer.DrawRoundedImage(frame.Image, image, 6, Color.White.PushOpacity(progress));
            }

            else
            {
                renderer.FillRoundedRect(rect, 6, theme.Panel.PushOpacity(progress));
                DrawCaption(renderer, "Preview unavailable", rect.X, rect.Y + rect.Height / 2, rect.Width, theme.Text.SmallSize, theme.Text.MutedColor.PushOpacity(progress));
            }

            if (preview.Address == context.SelectedAddress)
            {
                renderer.FillRoundedBorder(new Rect(rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8),
                                        10, theme.Border.Width, Color.White.PushOpacity(progress));
            }

            var iconSize = Math.Min(THUMBNAIL_ICON_SIZE * scale, rect.Width);
            var iconRect = new Rect(rect.X + (rect.Width - iconSize) / 2,
                rect.Y + THUMBNAIL_ICON_TOP * scale, iconSize, iconSize);
            var iconShadow = Color.FromRgb(0, 0, 0, 0.45f * progress);
            if (preview.IconPath is { } iconPath)
            {
                renderer.DrawImageShadow(iconPath, iconRect, iconShadow, 6 * scale,
                    offsetY: 2 * scale, loadAsync: true);
                renderer.DrawImage(iconPath, iconRect, Color.White.PushOpacity(progress), loadAsync: true);
            }

            else
            {
                renderer.DrawImageShadow(Icons.Application, iconRect, iconShadow, 6 * scale, offsetY: 2 * scale);
                renderer.DrawImage(Icons.Application, iconRect, theme.Text.Color, opacity: progress);
            }

            var workspaceText = preview.WorkspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var workspaceTextWidth = renderer.MeasureText(workspaceText, theme.Text.HeaderSize);
            var badgeWidth = workspaceTextWidth + 20;
            var badgeHeight = theme.Text.HeaderSize + 12;
            var badgeX = rect.Width >= badgeWidth + 2 * WORKSPACE_BADGE_PADDING
                ? rect.X + WORKSPACE_BADGE_PADDING
                : rect.X + (rect.Width - badgeWidth) / 2;
            var badge = new Rect(badgeX, rect.Y + rect.Height - WORKSPACE_BADGE_PADDING - badgeHeight, badgeWidth, badgeHeight);
            renderer.FillRoundedRect(badge, 6, theme.Panel.PushOpacity(progress));
            renderer.FillRoundedBorder(badge, 6, 1, theme.Border.Color.PushOpacity(progress));
            renderer.DrawText(workspaceText, badge.X + (badgeWidth - workspaceTextWidth) / 2,
                badge.Y + (badgeHeight + theme.Text.HeaderSize * 0.72f) / 2,
                theme.Text.HeaderSize, theme.Text.Color.PushOpacity(progress));

            DrawCaption(renderer, preview.Title, rect.X, rect.Y + rect.Height + 20, rect.Width, theme.Text.Size, theme.Text.Color.PushOpacity(progress));
            DrawCaption(renderer, preview.Subtitle, rect.X, rect.Y + rect.Height + 38, rect.Width, theme.Text.SmallSize, theme.Text.MutedColor.PushOpacity(progress));
            var finalHit = new Rect(rect.X, rect.Y, rect.Width, rect.Height + THUMBNAIL_CAPTION_HEIGHT);
            if (context.Interactive && !pointerOverSearch && Layout.Input.HasPointer && Layout.Input.PointerPressed
                && finalHit.Contains(Layout.Input.PointerX, Layout.Input.PointerY))
            {
                activated = preview.Address;
            }
        }

        return activated;
    }
}

using HyprNetShell.Core.Assets;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Overview;

internal sealed class CrystalOverviewDrawer : OverviewDrawer
{
    private const float THUMBNAILS_SCALE = OverviewController.THUMBNAILS_SCALE;
    private const float THUMBNAILS_GAP = 32f;
    private const int OVERVIEW_PADDING = 24;
    private const float SURFACE_SPHERE_SCALE = 1.12f;
    private const float SPHERE_SCALE = 1.8f;
    private const int PREVIEW_ROWS = 3;
    private const float SPHERE_TRANSITION_SCALE = 1.4f;

    public override string? Draw(IRenderApi renderer, OverviewDrawContext context)
    {
        DrawBackground(renderer, context);
        var (width, height, x, y) = (context.Width, context.Height, context.X, context.Y);
        var transitionProgress = context.TransitionProgress;
        var progress = context.Progress;
        var previewTransition = context.PreviewTransition;
        var pointerOverSearch = context.PointerOverOverlay;
        var now = context.Now;
        var dt = context.DeltaTime;
        var theme = ThemeManager.Current;
        string? activated = null;
        var sphereBounds = GetSurfaceSphere(width, height);
        var center = new Point(sphereBounds.X + sphereBounds.Width / 2, sphereBounds.Y + sphereBounds.Height / 2);
        var zoom = 1 + (SPHERE_TRANSITION_SCALE - 1) * (1 - transitionProgress);
        var rotation = new Point(0, 0);
        var focusedAddress = context.FocusedAddress;
        var transitionPreview = context.Previews.FirstOrDefault(preview => preview.Address == focusedAddress);
        if (transitionPreview is not null)
        {
            var anchor = new Point(transitionPreview.Bounds.X + transitionPreview.Bounds.Width / 2,
                transitionPreview.Bounds.Y + transitionPreview.Bounds.Height / 2);
            var targetRotation = SphereProjection.CenterRotation(anchor, sphereBounds);
            rotation = new Point(targetRotation.X * (1 - transitionProgress), targetRotation.Y * (1 - transitionProgress));
        }

        Rect Transform(Rect rect) => new(x + center.X + (rect.X - center.X) * zoom,
            y + center.Y + (rect.Y - center.Y) * zoom, rect.Width * zoom, rect.Height * zoom);

        renderer.DrawCrystalSphere(Transform(GetSphere(width, height)), (float)(now % 3600), progress);
        var sphere = Transform(sphereBounds);
        var drawPreviews = !previewTransition ? (IEnumerable<OverviewPreview>)context.Previews
            : context.Previews.OrderBy(preview => preview.Address == focusedAddress);
        foreach (var preview in drawPreviews)
        {
            var warpStrength = SphereProjection.THUMBNAIL_WARP_STRENGTH;
            var previewRotation = rotation;
            var isFocusTarget = previewTransition && preview.Address == focusedAddress;
            if (isFocusTarget)
            {
                warpStrength *= transitionProgress;
                var anchor = new Point(preview.Bounds.X + preview.Bounds.Width / 2,
                    preview.Bounds.Y + preview.Bounds.Height / 2);
                var targetRotation = SphereProjection.CenterRotation(anchor, sphereBounds, warpStrength);
                previewRotation = new Point(targetRotation.X * (1 - transitionProgress), targetRotation.Y * (1 - transitionProgress));
            }

            var bounds = Transform(preview.Bounds);
            if (isFocusTarget)
            {
                bounds = ScaleFocusTarget(bounds, preview, transitionProgress, width, height);
            }

            var hover = Hover.GetValueOrDefault(preview.Address);
            var previousScale = THUMBNAILS_SCALE * (isFocusTarget
                ? 1 + 0.035f * hover * transitionProgress
                : 0.94f + 0.06f * transitionProgress + 0.035f * hover);
            var previousLift = isFocusTarget ? -7 * hover * transitionProgress : 18 * (1 - transitionProgress) - 7 * hover;
            var previousSource = new Rect(bounds.X + bounds.Width * (1 - previousScale) / 2,
                bounds.Y + bounds.Height * (1 - previousScale) / 2 + previousLift,
                bounds.Width * previousScale, bounds.Height * previousScale);
            previousSource = FitImage(previousSource, preview.Frame?.Image);
            var projected = SphereProjection.ProjectBounds(previousSource, sphere, previewRotation, warpStrength);
            var captionHit = new Rect(projected.X, projected.Y + projected.Height, projected.Width, THUMBNAIL_CAPTION_HEIGHT);
            var pointer = SphereProjection.Unproject(new Point(Layout.Input.PointerX, Layout.Input.PointerY), sphere, previewRotation, warpStrength);
            var hovered = context.Interactive && !pointerOverSearch && Layout.Input.HasPointer
                && (ContainsImage(previousSource, pointer) || captionHit.Contains(Layout.Input.PointerX, Layout.Input.PointerY));
            hover += ((hovered ? 1 : 0) - hover) * (float)(1 - Math.Exp(-dt / 0.07));
            Hover[preview.Address] = hover;
            var scale = THUMBNAILS_SCALE * (isFocusTarget
                ? 1 + 0.035f * hover * transitionProgress
                : 0.94f + 0.06f * transitionProgress + 0.035f * hover);
            var lift = isFocusTarget ? -7 * hover * transitionProgress : 18 * (1 - transitionProgress) - 7 * hover;
            var source = new Rect(bounds.X + bounds.Width * (1 - scale) / 2,
                bounds.Y + bounds.Height * (1 - scale) / 2 + lift,
                bounds.Width * scale, bounds.Height * scale);

            var imageSource = FitImage(source, preview.Frame?.Image);

            renderer.DrawSphericalImage(preview.Frame?.Image, imageSource, sphere, Color.White.PushOpacity(progress),
                preview.Address == context.SelectedAddress ? 1 : hover * 0.6f, previewRotation, warpStrength);
            var rect = SphereProjection.ProjectBounds(imageSource, sphere, previewRotation, warpStrength);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            if (preview.Frame is null)
            {
                DrawCaption(renderer, "Preview unavailable", rect.X, rect.Y + rect.Height / 2, rect.Width, theme.Text.SmallSize, theme.Text.MutedColor.PushOpacity(progress));
            }

            var iconSize = rect.Width >= 48 && rect.Height >= 48 ? Math.Min(THUMBNAIL_ICON_SIZE * scale, rect.Width) : 0;
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
            if (rect.Width >= badgeWidth + 2 * WORKSPACE_BADGE_PADDING && rect.Height >= badgeHeight + 2 * WORKSPACE_BADGE_PADDING)
            {
                renderer.FillRoundedRect(badge, 6, theme.Panel.PushOpacity(progress));
                renderer.FillRoundedBorder(badge, 6, 1, theme.Border.Color.PushOpacity(progress));
                renderer.DrawText(workspaceText, badge.X + (badgeWidth - workspaceTextWidth) / 2,
                    badge.Y + (badgeHeight + theme.Text.HeaderSize * 0.72f) / 2,
                    theme.Text.HeaderSize, theme.Text.Color.PushOpacity(progress));
            }

            DrawCaption(renderer, preview.Title, rect.X, rect.Y + rect.Height + 20, rect.Width, theme.Text.Size, theme.Text.Color.PushOpacity(progress));
            DrawCaption(renderer, preview.Subtitle, rect.X, rect.Y + rect.Height + 38, rect.Width, theme.Text.SmallSize, theme.Text.MutedColor.PushOpacity(progress));
            var finalCaptionHit = new Rect(rect.X, rect.Y + rect.Height, rect.Width, THUMBNAIL_CAPTION_HEIGHT);
            if (context.Interactive && !pointerOverSearch && Layout.Input.HasPointer && Layout.Input.PointerPressed
                && (ContainsImage(imageSource, pointer) || finalCaptionHit.Contains(Layout.Input.PointerX, Layout.Input.PointerY)))
            {
                activated = preview.Address;
            }
        }

        return activated;
    }

    private Rect ScaleFocusTarget(Rect bounds, OverviewPreview preview, float transitionProgress, int outputWidth, int outputHeight)
    {
        var window = (Width: preview.WindowWidth, Height: preview.WindowHeight);
        if (window.Width <= 0 || window.Height <= 0)
        {
            return bounds;
        }

        var image = FitImage(preview.Bounds, preview.Frame?.Image);
        var finalWidth = image.Width * SPHERE_TRANSITION_SCALE * THUMBNAILS_SCALE;
        var finalHeight = image.Height * SPHERE_TRANSITION_SCALE * THUMBNAILS_SCALE;
        var targetScale = Math.Min(Math.Min(window.Width, outputWidth) / Math.Max(1, finalWidth),
            Math.Min(window.Height, outputHeight) / Math.Max(1, finalHeight));
        var scale = 1 + (targetScale - 1) * (1 - transitionProgress);
        return new Rect(bounds.X + bounds.Width * (1 - scale) / 2,
            bounds.Y + bounds.Height * (1 - scale) / 2, bounds.Width * scale, bounds.Height * scale);
    }

    private static Rect GetSphere(int width, int height)
    {
        var diameter = Math.Max(1, Math.Min(height * 0.78f, width * 0.88f)) * SPHERE_SCALE;
        return new Rect((width - diameter) / 2, height * 0.54f - diameter / 2, diameter, diameter);
    }

    private static Rect GetSurfaceSphere(int width, int height)
    {
        var sphere = GetSphere(width, height);
        var diameter = sphere.Width * SURFACE_SPHERE_SCALE;
        return new Rect(sphere.X - (diameter - sphere.Width) / 2,
            sphere.Y - (diameter - sphere.Height) / 2, diameter, diameter);
    }

    public override Point ProjectCenter(Rect bounds, int width, int height) =>
        SphereProjection.Project(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2), GetSurfaceSphere(width, height));

    public override Rect[] BuildLayout(int width, int height, float[] ratios)
    {
        var result = new Rect[ratios.Length];
        if (ratios.Length == 0)
        {
            return result;
        }

        var sphere = GetSurfaceSphere(width, height);
        var availableHeight = Math.Max(1, Math.Min(sphere.Height * 0.88f, height - 160));
        var captionSpace = THUMBNAIL_CAPTION_HEIGHT;
        var radius = sphere.Width * 0.47f;

        var count = Math.Min(PREVIEW_ROWS, ratios.Length);
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

        var low = 0f;
        var high = Math.Max(0, Math.Min(680, (availableHeight - (count - 1) * THUMBNAILS_GAP - count * captionSpace) / count));
        for (var step = 0; step < 24; step++)
        {
            var candidate = (low + high) / 2;
            var totalHeight = count * (candidate + captionSpace) + (count - 1) * THUMBNAILS_GAP;
            var fits = true;
            for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                var latitude = -totalHeight / 2 + rowIndex * (candidate + captionSpace + THUMBNAILS_GAP) + candidate / 2;
                var chord = Math.Min(width - 2 * OVERVIEW_PADDING,
                    2 * MathF.Sqrt(Math.Max(0, radius * radius - latitude * latitude)));
                var required = row.Sum(i => ratios[i] * candidate) + (row.Count - 1) * THUMBNAILS_GAP;
                if (required > chord)
                {
                    fits = false;
                    break;
                }
            }

            if (fits)
            {
                low = candidate;
            }

            else
            {
                high = candidate;
            }
        }

        var bestHeight = low;
        var bestRows = rows;

        var y = sphere.Y + sphere.Height / 2 - (bestRows.Count * (bestHeight + captionSpace) + (bestRows.Count - 1) * THUMBNAILS_GAP) / 2;
        foreach (var row in bestRows)
        {
            var rowWidth = row.Sum(i => ratios[i] * bestHeight) + (row.Count - 1) * THUMBNAILS_GAP;
            var x = (width - rowWidth) / 2;
            foreach (var i in row)
            {
                var w = ratios[i] * bestHeight;
                var cell = new Rect(x, y, w, bestHeight);
                var patch = SphereProjection.FitSurfacePatch(cell, ratios[i], sphere);
                result[i] = patch;
                x += w + THUMBNAILS_GAP;
            }

            y += bestHeight + captionSpace + THUMBNAILS_GAP;
        }

        return result;
    }
}

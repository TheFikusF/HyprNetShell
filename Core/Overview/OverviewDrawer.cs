using HyprNetShell.GUI;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Overview;

internal abstract class OverviewDrawer : IOverviewDrawer
{
    protected const float THUMBNAIL_CAPTION_HEIGHT = 44f;
    protected const float THUMBNAIL_ICON_SIZE = 52f;
    protected const float THUMBNAIL_ICON_TOP = -12f;
    protected const float WORKSPACE_BADGE_PADDING = 12f;
    protected readonly Dictionary<string, float> Hover = [];
    private readonly Dictionary<(string Text, float Width, float Size), (string Text, float Width)> _captions = [];

    public abstract Rect[] BuildLayout(int width, int height, float[] ratios);
    public abstract Point ProjectCenter(Rect bounds, int width, int height);
    public abstract string? Draw(IRenderApi renderer, OverviewDrawContext context);

    public void Reset()
    {
        Hover.Clear();
        _captions.Clear();
    }

    protected static void DrawBackground(IRenderApi renderer, OverviewDrawContext context)
    {
        var (width, height, x, y) = (context.Width, context.Height, context.X, context.Y);
        var image = context.Background;
        var progress = context.Progress;
        var bounds = new Rect(x, y, width, height);
        if (image is null)
        {
            renderer.FillRect(bounds, ThemeManager.Current.Panel.PushOpacity(progress));
            return;
        }

        // Overview fills the output; the renderer viewport clips the centered cover image.
        var scale = Math.Max((float)width / image.Width, (float)height / image.Height);
        var w = image.Width * scale;
        var h = image.Height * scale;
        renderer.DrawImage(image, new Rect(x + (width - w) / 2, y + (height - h) / 2, w, h),
            Color.White.PushOpacity(progress));
        renderer.FillRect(bounds, Color.FromRgb(0, 0, 0, .48f * progress));
    }

    protected static Rect FitImage(Rect source, RawImageData? image)
    {
        if (image is null)
        {
            return source;
        }

        var fit = Math.Min(source.Width / image.Width, source.Height / image.Height);
        return new Rect(source.X + (source.Width - image.Width * fit) / 2,
            source.Y + (source.Height - image.Height * fit) / 2, image.Width * fit, image.Height * fit);
    }

    protected static bool ContainsImage(Rect source, Point pointer)
    {
        if (!source.Contains(pointer.X, pointer.Y))
        {
            return false;
        }

        var radius = Math.Min(8, Math.Min(source.Width, source.Height) * 0.12f);
        var dx = Math.Max(0, Math.Abs(pointer.X - source.X - source.Width / 2) - source.Width / 2 + radius);
        var dy = Math.Max(0, Math.Abs(pointer.Y - source.Y - source.Height / 2) - source.Height / 2 + radius);
        return dx * dx + dy * dy <= radius * radius;
    }

    public void DrawCaption(IRenderApi renderer, string text, float x, float y, float width, float size, Color color)
    {
        if (width <= 0)
        {
            return;
        }

        // Round down so cached fitting never overflows animated bounds, without subpixel hover churn.
        var fitWidth = MathF.Floor(width / 4) * 4;
        if (fitWidth < renderer.MeasureText("…", size))
        {
            return;
        }

        var key = (text, fitWidth, size);
        if (!_captions.TryGetValue(key, out var caption))
        {
            var measuredWidth = renderer.MeasureText(text, size);
            if (measuredWidth > fitWidth)
            {
                var low = 0;
                var high = text.Length;
                while (low < high)
                {
                    var mid = (low + high + 1) / 2;
                    if (renderer.MeasureText(text[..mid] + "…", size) <= fitWidth)
                    {
                        low = mid;
                    }

                    else
                    {
                        high = mid - 1;
                    }
                }

                text = text[..low] + "…";
                measuredWidth = renderer.MeasureText(text, size);
            }

            caption = (text, measuredWidth);
            if (_captions.Count >= 2048)
            {
                _captions.Clear();
            }

            _captions.Add(key, caption);
        }

        renderer.DrawText(caption.Text, x + Math.Max(0, (width - caption.Width) / 2), y, size, color);
    }
}

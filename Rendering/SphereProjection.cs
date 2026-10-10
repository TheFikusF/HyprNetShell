using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering;

/// <summary>Radial projection of absolute screen-plane coordinates about a square sphere's center.</summary>
public static partial class SphereProjection
{
    /// <summary>0 disables thumbnail curvature; 1 applies the full spherical projection.</summary>
    public const float THUMBNAIL_WARP_STRENGTH = 0.5f;
    public static Point Project(Point point, Rect sphere, Point rotation = default, float warpStrength = THUMBNAIL_WARP_STRENGTH)
    {
        ValidateSphere(sphere);
        if (rotation.X != 0 || rotation.Y != 0)
        {
            return ProjectRotated(point, sphere, rotation, warpStrength);
        }

        var radius = sphere.Width * 0.5;
        var centerX = sphere.X + radius;
        var centerY = sphere.Y + radius;
        var x = point.X - centerX;
        var y = point.Y - centerY;
        var scale = 1 / Math.Sqrt(1 + warpStrength * (x * x + y * y) / (radius * radius));
        return new Point((float)(centerX + x * scale), (float)(centerY + y * scale));
    }

    /// <summary>Encloses all projected edges, including extrema between the corners.</summary>
    public static Rect ProjectBounds(Rect source, Rect sphere, Point rotation = default, float warpStrength = THUMBNAIL_WARP_STRENGTH)
    {
        ValidateSphere(sphere);
        if (!float.IsFinite(source.X) || !float.IsFinite(source.Y)
            || !float.IsFinite(source.Width) || !float.IsFinite(source.Height)
            || source.Width < 0 || source.Height < 0
            || !float.IsFinite(source.X + source.Width) || !float.IsFinite(source.Y + source.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (rotation.X != 0 || rotation.Y != 0)
        {
            return ProjectRotatedBounds(source, sphere, rotation, warpStrength);
        }

        var centerX = sphere.X + sphere.Width * 0.5f;
        var centerY = sphere.Y + sphere.Height * 0.5f;
        var nearestX = Math.Clamp(centerX, source.X, source.X + source.Width);
        var nearestY = Math.Clamp(centerY, source.Y, source.Y + source.Height);
        ReadOnlySpan<Point> candidates = stackalloc Point[]
        {
            Project(new Point(source.X, source.Y), sphere, warpStrength: warpStrength),
            Project(new Point(source.X + source.Width, source.Y), sphere, warpStrength: warpStrength),
            Project(new Point(source.X, source.Y + source.Height), sphere, warpStrength: warpStrength),
            Project(new Point(source.X + source.Width, source.Y + source.Height), sphere, warpStrength: warpStrength),
            Project(new Point(source.X, nearestY), sphere, warpStrength: warpStrength),
            Project(new Point(source.X + source.Width, nearestY), sphere, warpStrength: warpStrength),
            Project(new Point(nearestX, source.Y), sphere, warpStrength: warpStrength),
            Project(new Point(nearestX, source.Y + source.Height), sphere, warpStrength: warpStrength),
        };
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;
        foreach (var point in candidates)
        {
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>Fits an aspect-preserving source patch into a screen-space cell on the visible sphere.</summary>
    public static Rect FitSurfacePatch(Rect cell, float aspect, Rect sphere)
    {
        ValidateSphere(sphere);
        if (cell.Width <= 0 || cell.Height <= 0 || !float.IsFinite(aspect) || aspect <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cell));
        }

        var target = new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
        var center = Unproject(target, sphere);
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y))
        {
            throw new ArgumentOutOfRangeException(nameof(cell), "Surface cell center must be inside the sphere.");
        }

        var source = new Rect(center.X, center.Y, 0, 0);
        // Compensate for unequal foreshortening and the curved patch's shifted bounding-box center.
        for (var iteration = 0; iteration < 10; iteration++)
        {
            var low = 0f;
            var high = sphere.Width * 16;
            for (var step = 0; step < 28; step++)
            {
                var h = (low + high) / 2;
                var candidate = new Rect(center.X - h * aspect / 2, center.Y - h / 2, h * aspect, h);
                var bounds = ProjectBounds(candidate, sphere);
                if (bounds.Width <= cell.Width && bounds.Height <= cell.Height)
                {
                    low = h;
                }

                else
                {
                    high = h;
                }
            }

            source = new Rect(center.X - low * aspect / 2, center.Y - low / 2, low * aspect, low);
            var projected = ProjectBounds(source, sphere);
            var offsetX = target.X - projected.X - projected.Width / 2;
            var offsetY = target.Y - projected.Y - projected.Height / 2;
            if (Math.Abs(offsetX) + Math.Abs(offsetY) < 0.05f)
            {
                break;
            }

            var projectedCenter = Project(center, sphere);
            var corrected = Unproject(new Point(projectedCenter.X + offsetX, projectedCenter.Y + offsetY), sphere);
            if (!float.IsFinite(corrected.X) || !float.IsFinite(corrected.Y))
            {
                break;
            }

            center = corrected;
        }

        return source;
    }

    /// <summary>Returns NaN coordinates beyond the projection's finite inverse domain.</summary>
    public static Point Unproject(Point point, Rect sphere, Point rotation = default, float warpStrength = THUMBNAIL_WARP_STRENGTH)
    {
        ValidateSphere(sphere);
        if (rotation.X != 0 || rotation.Y != 0)
        {
            return UnprojectRotated(point, sphere, rotation, warpStrength);
        }

        var radius = sphere.Width * 0.5;
        var centerX = sphere.X + radius;
        var centerY = sphere.Y + radius;
        var x = point.X - centerX;
        var y = point.Y - centerY;
        var denominator = 1 - warpStrength * (x * x + y * y) / (radius * radius);
        if (denominator <= 0)
        {
            return new Point(float.NaN, float.NaN);
        }

        var scale = 1 / Math.Sqrt(denominator);
        return new Point((float)(centerX + x * scale), (float)(centerY + y * scale));
    }

    internal static void ValidateSphere(Rect sphere)
    {
        if (!float.IsFinite(sphere.X) || !float.IsFinite(sphere.Y)
            || !float.IsFinite(sphere.Width) || sphere.Width <= 0 || sphere.Width != sphere.Height
            || !float.IsFinite(sphere.X + sphere.Width) || !float.IsFinite(sphere.Y + sphere.Height))
        {
            throw new ArgumentOutOfRangeException(nameof(sphere), "Sphere must be a finite, positive square.");
        }
    }
}

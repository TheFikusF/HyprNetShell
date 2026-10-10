using System.Numerics;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering;

public static partial class SphereProjection
{
    private const float ROTATION_EPSILON = 0.000001f;

    public static Point CenterRotation(Point sourceCenter, Rect sphere, float warpStrength = THUMBNAIL_WARP_STRENGTH)
    {
        ValidateSphere(sphere);
        var radius = RotationRadius(sphere, warpStrength);
        var x = sourceCenter.X - sphere.X - sphere.Width / 2;
        var y = sourceCenter.Y - sphere.Y - sphere.Height / 2;
        return new Point(-MathF.Atan2(x, radius), MathF.Atan2(y, MathF.Sqrt(x * x + radius * radius)));
    }

    private static float RotationRadius(Rect sphere, float warpStrength) => sphere.Width / (2 * MathF.Sqrt(Math.Max(warpStrength, ROTATION_EPSILON)));

    private static Vector3 RotateDirection(Vector3 direction, Point rotation, bool inverse = false)
    {
        var yawCos = MathF.Cos(rotation.X);
        var yawSin = MathF.Sin(rotation.X);
        var pitchCos = MathF.Cos(rotation.Y);
        var pitchSin = MathF.Sin(rotation.Y);
        if (inverse)
        {
            var y = pitchCos * direction.Y + pitchSin * direction.Z;
            var z = -pitchSin * direction.Y + pitchCos * direction.Z;
            return new Vector3(yawCos * direction.X - yawSin * z, y, yawSin * direction.X + yawCos * z);
        }

        var rotatedX = yawCos * direction.X + yawSin * direction.Z;
        var rotatedZ = -yawSin * direction.X + yawCos * direction.Z;
        return new Vector3(rotatedX, pitchCos * direction.Y - pitchSin * rotatedZ,
            pitchSin * direction.Y + pitchCos * rotatedZ);
    }

    private static Point ProjectRotated(Point point, Rect sphere, Point rotation, float warpStrength)
    {
        var radius = RotationRadius(sphere, warpStrength);
        var center = new Point(sphere.X + sphere.Width / 2, sphere.Y + sphere.Height / 2);
        var direction = Vector3.Normalize(RotateDirection(new Vector3(point.X - center.X, point.Y - center.Y, radius), rotation));
        if (direction.Z < -ROTATION_EPSILON)
        {
            return new Point(float.NaN, float.NaN);
        }

        return new Point(center.X + direction.X * radius, center.Y + direction.Y * radius);
    }

    private static Point UnprojectRotated(Point point, Rect sphere, Point rotation, float warpStrength)
    {
        var radius = RotationRadius(sphere, warpStrength);
        var center = new Point(sphere.X + sphere.Width / 2, sphere.Y + sphere.Height / 2);
        var x = (point.X - center.X) / radius;
        var y = (point.Y - center.Y) / radius;
        var depth = 1 - x * x - y * y;
        if (depth < -ROTATION_EPSILON)
        {
            return new Point(float.NaN, float.NaN);
        }

        var direction = RotateDirection(new Vector3(x, y, MathF.Sqrt(Math.Max(0, depth))), rotation, inverse: true);
        if (direction.Z <= ROTATION_EPSILON)
        {
            return new Point(float.NaN, float.NaN);
        }

        return new Point(center.X + radius * direction.X / direction.Z,
            center.Y + radius * direction.Y / direction.Z);
    }

    private static Rect ProjectRotatedBounds(Rect source, Rect sphere, Point rotation, float warpStrength)
    {
        var radius = RotationRadius(sphere, warpStrength);
        var center = new Point(sphere.X + sphere.Width / 2, sphere.Y + sphere.Height / 2);
        var left = float.PositiveInfinity;
        var top = float.PositiveInfinity;
        var right = float.NegativeInfinity;
        var bottom = float.NegativeInfinity;

        void AddProjected(Point point)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            {
                return;
            }

            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }

        void AddEdge(Point start, Point end)
        {
            var first = new Vector3(start.X - center.X, start.Y - center.Y, radius);
            var delta = new Vector3(end.X - start.X, end.Y - start.Y, 0);
            var rotatedFirst = RotateDirection(first, rotation);
            var rotatedDelta = RotateDirection(delta, rotation);
            var c = Vector3.Dot(first, first);
            var d = 2 * Vector3.Dot(first, delta);
            var e = Vector3.Dot(delta, delta);

            void AddAt(float t)
            {
                if (float.IsFinite(t) && t >= 0 && t <= 1)
                {
                    AddProjected(ProjectRotated(new Point(start.X + delta.X * t, start.Y + delta.Y * t), sphere, rotation, warpStrength));
                }
            }

            // Coordinate extrema of (a + b*t) / sqrt(c + d*t + e*t*t), plus the visible-horizon crossing.
            AddAt(0);
            AddAt(1);
            AddAt(-(rotatedDelta.X * c - rotatedFirst.X * d / 2) / (rotatedDelta.X * d / 2 - rotatedFirst.X * e));
            AddAt(-(rotatedDelta.Y * c - rotatedFirst.Y * d / 2) / (rotatedDelta.Y * d / 2 - rotatedFirst.Y * e));
            AddAt(-rotatedFirst.Z / rotatedDelta.Z);
        }

        var topLeft = new Point(source.X, source.Y);
        var topRight = new Point(source.X + source.Width, source.Y);
        var bottomLeft = new Point(source.X, source.Y + source.Height);
        var bottomRight = new Point(source.X + source.Width, source.Y + source.Height);
        AddEdge(topLeft, topRight);
        AddEdge(topRight, bottomRight);
        AddEdge(bottomRight, bottomLeft);
        AddEdge(bottomLeft, topLeft);
        ReadOnlySpan<Point> poles = stackalloc Point[]
        {
            new(center.X - radius, center.Y), new(center.X + radius, center.Y),
            new(center.X, center.Y - radius), new(center.X, center.Y + radius),
        };
        foreach (var pole in poles)
        {
            var inverse = UnprojectRotated(pole, sphere, rotation, warpStrength);
            if (source.Contains(inverse.X, inverse.Y))
            {
                AddProjected(pole);
            }
        }

        if (!float.IsFinite(left))
        {
            return new Rect(center.X, center.Y, 0, 0);
        }

        return new Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}

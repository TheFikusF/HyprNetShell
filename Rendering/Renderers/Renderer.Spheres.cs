using HyprNetShell.Rendering.ShaderModels;
using HyprNetShell.Rendering.Primitives;
using System.Numerics;

namespace HyprNetShell.Rendering.Renderers;

public sealed unsafe partial class Renderer
{
    private readonly CrystalSphereShader _crystalSphere;
    private readonly SphericalImageShader _sphericalImage;

    public void DrawCrystalSphere(Rect sphere, float time, float opacity)
    {
        SphereProjection.ValidateSphere(sphere);
        if (!float.IsFinite(time) || !float.IsFinite(opacity))
        {
            throw new ArgumentOutOfRangeException(nameof(time), "Time and opacity must be finite.");
        }

        opacity = Math.Clamp(opacity, 0, 1);
        if (opacity == 0)
        {
            return;
        }

        var padding = MathF.Max(4, sphere.Width * 0.15f);
        var bounds = new Rect(sphere.X - padding, sphere.Y - padding,
            sphere.Width + 2 * padding, sphere.Height + 2 * padding);
        FlushPendingGeometry();
        _crystalSphere.Sphere = new Vector4(sphere.X, sphere.Y, sphere.Width, sphere.Height);
        _crystalSphere.DrawRect = new Vector4(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _crystalSphere.Time = time;
        DrawTexture(new Texture(0), bounds, new Color(1, 1, 1, opacity), _crystalSphere);
        FlushTextureGeometry();
    }

    public void DrawSphericalImage(RawImageData? image, Rect sourceRect, Rect sphere, Color color, float emphasis, Point rotation = default, float warpStrength = SphereProjection.THUMBNAIL_WARP_STRENGTH)
    {
        var bounds = SphereProjection.ProjectBounds(sourceRect, sphere, rotation, warpStrength);
        if (!float.IsFinite(emphasis))
        {
            throw new ArgumentOutOfRangeException(nameof(emphasis));
        }

        if (sourceRect.Width <= 0 || sourceRect.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0 || color.A <= 0)
        {
            return;
        }

        // Include the fragment shader's antialiased edge outside the analytic bounds.
        bounds = new Rect(bounds.X - 1, bounds.Y - 1, bounds.Width + 2, bounds.Height + 2);
        FlushPendingGeometry();
        var texture = image is null ? new Texture(0) : _textureRepository.GetTexture(image);
        _sphericalImage.Sphere = new Vector4(sphere.X, sphere.Y, sphere.Width, sphere.Height);
        _sphericalImage.DrawRect = new Vector4(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _sphericalImage.SourceRect = new Vector4(sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height);
        _sphericalImage.HasImage = image is not null;
        _sphericalImage.WarpStrength = warpStrength;
        _sphericalImage.SurfaceRotation = new Vector2(rotation.X, rotation.Y);
        _sphericalImage.Emphasis = Math.Clamp(emphasis, 0, 1);
        DrawTexture(texture, bounds, color, _sphericalImage);
        FlushTextureGeometry();
    }
}

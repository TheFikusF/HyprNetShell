using System.Numerics;

namespace HyprNetShell.Rendering.ShaderModels;

[Shader("Shaders/textured.vert.glsl", "Shaders/spherical-image.frag.glsl")]
internal sealed partial class SphericalImageShader : ITextureBatchShader
{
    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }

    [Uniform("uTexture")]
    public partial int Texture { get; set; }

    [Uniform("uSphere")]
    public partial Vector4 Sphere { get; set; }

    [Uniform("uDrawRect")]
    public partial Vector4 DrawRect { get; set; }

    [Uniform("uSourceRect")]
    public partial Vector4 SourceRect { get; set; }

    [Uniform("uHasImage")]
    public partial bool HasImage { get; set; }

    [Uniform("uEmphasis")]
    public partial float Emphasis { get; set; }

    [Uniform("uWarpStrength")]
    public partial float WarpStrength { get; set; }

    [Uniform("uSurfaceRotation")]
    public partial Vector2 SurfaceRotation { get; set; }

    public void PrepareTextureBatch(Vector2 viewport)
    {
        Bind();
        Viewport = viewport;
        Texture = 0;
    }
}

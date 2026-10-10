using System.Numerics;

namespace HyprNetShell.Rendering.ShaderModels;

[Shader("Shaders/textured.vert.glsl", "Shaders/crystal-sphere.frag.glsl")]
internal sealed partial class CrystalSphereShader : ITextureBatchShader
{
    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }

    [Uniform("uSphere")]
    public partial Vector4 Sphere { get; set; }

    [Uniform("uDrawRect")]
    public partial Vector4 DrawRect { get; set; }

    [Uniform("uTime")]
    public partial float Time { get; set; }

    public void PrepareTextureBatch(Vector2 viewport)
    {
        Bind();
        Viewport = viewport;
    }
}

using System.Numerics;

namespace HyprNetShell.Rendering.ShaderModels;

[Shader("Shaders/textured.vert.glsl", "Shaders/alpha-texture.frag.glsl")]
internal sealed partial class AlphaTextureShader
{
    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }

    [Uniform("uAtlas")]
    public partial int Atlas { get; set; }

    [Uniform("uColor")]
    public partial Vector4 Color { get; set; }
}

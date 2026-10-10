using System.Numerics;

namespace HyprNetShell.Rendering.ShaderModels;

[Shader("Shaders/colored.vert.glsl", "Shaders/colored.frag.glsl")]
internal sealed partial class ColoredShader
{
    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }
}

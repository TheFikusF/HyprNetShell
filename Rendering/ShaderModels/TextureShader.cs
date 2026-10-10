using System.Numerics;

namespace HyprNetShell.Rendering.ShaderModels;

[Shader("Shaders/textured.vert.glsl", "Shaders/texture.frag.glsl")]
internal sealed partial class TextureShader : ITextureBatchShader
{
    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }

    [Uniform("uTexture")]
    public partial int Texture { get; set; }

    [Uniform("uColor")]
    public partial Vector4 Color { get; set; }

    [Uniform("uUseVertexColor")]
    public partial bool UseVertexColor { get; set; }

    [Uniform("uCornerRadius")]
    public partial float CornerRadius { get; set; }

    [Uniform("uImageSize")]
    public partial Vector2 ImageSize { get; set; }

    public void PrepareTextureBatch(Vector2 viewport)
    {
        Bind();
        Viewport = viewport;
        Texture = 0;
        UseVertexColor = true;
    }
}

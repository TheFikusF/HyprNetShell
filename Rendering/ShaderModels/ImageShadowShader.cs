using System.Numerics;

namespace HyprNetShell.Rendering.ShaderModels;

[Shader("Shaders/textured.vert.glsl", "Shaders/image-shadow.frag.glsl")]
internal sealed partial class ImageShadowShader : ITextureBatchShader
{
    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }

    [Uniform("uTexture")]
    public partial int Texture { get; set; }

    [Uniform("uBlurStep")]
    public partial Vector2 BlurStep { get; set; }

    public void PrepareTextureBatch(Vector2 viewport)
    {
        Bind();
        Viewport = viewport;
        Texture = 0;
    }
}

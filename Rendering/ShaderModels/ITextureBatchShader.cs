using System.Numerics;

namespace HyprNetShell.Rendering.ShaderModels;

internal interface ITextureBatchShader
{
    void PrepareTextureBatch(Vector2 viewport);
}

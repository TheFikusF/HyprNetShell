using System.Numerics;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Rendering.ShaderModels;

[Shader("Shaders/rounded.vert.glsl", "Shaders/rounded.frag.glsl")]
internal sealed partial class RoundedShader
{
    [Uniform("uGradientPositions")]
    public partial float[] GradientPositions { get; set; }

    [Uniform("uGradientColors")]
    public partial Vector4[] GradientColors { get; set; }

    [Uniform("uViewport")]
    public partial Vector2 Viewport { get; set; }

    [Uniform("uGradientDirection")]
    public partial int GradientDirection { get; set; }

    [Uniform("uGradientOffset")]
    public partial float GradientOffset { get; set; }

    [Uniform("uGradientStopCount")]
    public partial int GradientStopCount { get; set; }

    internal void UploadGradient(Gradient gradient, GradientDirection direction, float offset)
    {
        Bind();
        GradientDirection = (int)direction;
        GradientOffset = offset;

        var stopCount = gradient.Stops.Count;
        var positions = new float[stopCount];
        var colors = new Vector4[stopCount];
        for (var i = 0; i < stopCount; i++)
        {
            var stop = gradient.Stops[i];
            positions[i] = stop.Percent;
            colors[i] = new Vector4(stop.Color.R, stop.Color.G, stop.Color.B, stop.Color.A);
        }

        GradientStopCount = stopCount;
        GradientPositions = positions;
        GradientColors = colors;
    }
}

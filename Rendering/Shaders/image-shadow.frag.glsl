#version 330 core
in vec2 vTexCoord;
in vec4 vColor;
uniform sampler2D uTexture;
uniform vec2 uBlurStep;
out vec4 FragColor;

// Emulate a transparent border, including bilinear coverage at the outer texels.
// Repository textures use ClampToEdge, which otherwise extends opaque edges.
float maskAlpha(vec2 uv)
{
    vec2 size = vec2(textureSize(uTexture, 0));
    vec2 coverage = clamp(uv * size + 0.5, 0.0, 1.0)
                  * clamp((1.0 - uv) * size + 0.5, 0.0, 1.0);
    if (coverage.x * coverage.y == 0.0)
        return 0.0;
    return texture(uTexture, uv).a * coverage.x * coverage.y;
}

void main()
{
    if (uBlurStep == vec2(0.0))
    {
        FragColor = vec4(vColor.rgb, vColor.a * maskAlpha(vTexCoord));
        return;
    }
    // Samples span +/-3 sigma. Fixed work independent of texture size/radius.
    const float weights[9] = float[9](
        0.011108997, 0.079559509, 0.324652467, 0.754839602,
        1.0, 0.754839602, 0.324652467, 0.079559509, 0.011108997);
    float alpha = 0.0;
    float total = 0.0;
    for (int y = 0; y < 9; ++y)
    {
        for (int x = 0; x < 9; ++x)
        {
            float weight = weights[x] * weights[y];
            alpha += weight * maskAlpha(vTexCoord + vec2(x - 4, y - 4) * uBlurStep);
            total += weight;
        }
    }
    FragColor = vec4(vColor.rgb, vColor.a * alpha / total);
}

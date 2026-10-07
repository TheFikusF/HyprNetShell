#version 330 core
in vec2 vTexCoord;
uniform sampler2D uTexture;
uniform vec4 uColor;
uniform bool uUseVertexColor;
uniform float uCornerRadius;
uniform vec2 uImageSize;
in vec4 vColor;
out vec4 FragColor;

void main()
{
    FragColor = texture(uTexture, vTexCoord) * (uUseVertexColor ? vColor : uColor);
    if (uCornerRadius > 0.0)
    {
        vec2 position = (vTexCoord - 0.5) * uImageSize;
        vec2 edge = abs(position) - uImageSize * 0.5 + uCornerRadius;
        float distance = length(max(edge, 0.0)) + min(max(edge.x, edge.y), 0.0) - uCornerRadius;
        FragColor.a *= 1.0 - smoothstep(-0.5, 0.5, distance);
    }
}

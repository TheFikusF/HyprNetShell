#version 330 core
in vec2 vTexCoord;
uniform sampler2D uTexture;
uniform vec4 uColor;
uniform bool uUseVertexColor;
in vec4 vColor;
out vec4 FragColor;

void main()
{
    vec4 color = uUseVertexColor ? vColor : uColor;
    float alpha = texture(uTexture, vTexCoord).a;
    FragColor = vec4(color.rgb, color.a * alpha);
}

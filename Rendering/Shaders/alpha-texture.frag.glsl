#version 330 core
in vec2 vTexCoord;
uniform sampler2D uAtlas;
uniform vec4 uColor;
out vec4 FragColor;

void main()
{
    float alpha = texture(uAtlas, vTexCoord).r;
    FragColor = vec4(uColor.rgb, uColor.a * alpha);
}

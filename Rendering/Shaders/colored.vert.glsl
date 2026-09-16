#version 330 core
layout (location = 0) in vec2 aPosition;
layout (location = 1) in vec4 aColor;

uniform vec2 uViewport;
out vec4 vColor;

void main()
{
    vec2 zeroToOne = aPosition / uViewport;
    vec2 clip = zeroToOne * 2.0 - 1.0;
    gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
    vColor = aColor;
}

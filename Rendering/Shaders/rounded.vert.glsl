#version 330 core
layout (location = 0) in vec2 aUnitPosition;
layout (location = 1) in vec4 aRect;
layout (location = 2) in vec4 aRadii;
layout (location = 3) in vec4 aColor;
layout (location = 4) in vec4 aThickness;
layout (location = 5) in vec4 aInnerRadii;
layout (location = 6) in vec4 aParameters;

uniform vec2 uViewport;
out vec2 vLocalPosition;
flat out vec2 vSize;
flat out vec4 vRadii;
flat out vec4 vColor;
flat out int vMode;
flat out vec4 vThickness;
flat out vec4 vInnerRadii;
flat out float vShadowDistance;

void main()
{
    float padding = aParameters.z;
    vLocalPosition = aUnitPosition * (aRect.zw + 2.0 * padding) - padding;
    vec2 clip = (aRect.xy + vLocalPosition) / uViewport * 2.0 - 1.0;
    gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
    vSize = aRect.zw;
    vRadii = aRadii;
    vColor = aColor;
    vMode = int(aParameters.x);
    vThickness = aThickness;
    vInnerRadii = aInnerRadii;
    vShadowDistance = aParameters.y;
}

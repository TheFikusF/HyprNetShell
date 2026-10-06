#version 330 core
const int MODE_SOLID = 0;
const int MODE_BORDER = 1;
const int MODE_GRADIENT = 2;
const int MODE_SHADOW = 3;
const int MAX_GRADIENT_STOPS = 64;

in vec2 vLocalPosition;
flat in vec2 vSize;
flat in vec4 vRadii;
flat in vec4 vColor;
flat in int vMode;
flat in vec4 vThickness;
flat in vec4 vInnerRadii;
flat in float vShadowDistance;
uniform int uGradientDirection;
uniform float uGradientOffset;
uniform int uGradientStopCount;
uniform float uGradientPositions[MAX_GRADIENT_STOPS];
uniform vec4 uGradientColors[MAX_GRADIENT_STOPS];
out vec4 FragColor;

float boxDistance(vec2 point, vec2 size)
{
    vec2 centered = point - size * 0.5;
    vec2 distanceToEdge = abs(centered) - size * 0.5;
    return length(max(distanceToEdge, 0.0)) + min(max(distanceToEdge.x, distanceToEdge.y), 0.0);
}

float roundedRectDistance(vec2 point, vec2 size, vec4 radii)
{
    float radius;
    vec2 center;
    if (point.x < radii.x && point.y < radii.x)
    {
        radius = radii.x;
        center = vec2(radius);
    }
    else if (point.x > size.x - radii.y && point.y < radii.y)
    {
        radius = radii.y;
        center = vec2(size.x - radius, radius);
    }
    else if (point.x > size.x - radii.z && point.y > size.y - radii.z)
    {
        radius = radii.z;
        center = vec2(size.x - radius, size.y - radius);
    }
    else if (point.x < radii.w && point.y > size.y - radii.w)
    {
        radius = radii.w;
        center = vec2(radius, size.y - radius);
    }
    else
    {
        return boxDistance(point, size);
    }

    return length(point - center) - radius;
}

float coverage(float signedDistance)
{
    float antialiasWidth = max(fwidth(signedDistance), 0.0001);
    return clamp(0.5 - signedDistance / antialiasWidth, 0.0, 1.0);
}

vec4 gradientColor(float position)
{
    if (uGradientStopCount <= 1 || position <= uGradientPositions[0])
    {
        return uGradientColors[0];
    }

    for (int i = 1; i < MAX_GRADIENT_STOPS; ++i)
    {
        if (i >= uGradientStopCount || position <= uGradientPositions[i])
        {
            int upperIndex = min(i, uGradientStopCount - 1);
            float lowerPosition = uGradientPositions[upperIndex - 1];
            float upperPosition = uGradientPositions[upperIndex];
            float amount = upperPosition == lowerPosition
                ? 1.0
                : (position - lowerPosition) / (upperPosition - lowerPosition);
            return mix(uGradientColors[upperIndex - 1], uGradientColors[upperIndex], amount);
        }
    }

    return uGradientColors[uGradientStopCount - 1];
}

void main()
{
    float outerDistance = roundedRectDistance(vLocalPosition, vSize, vRadii);
    float alpha;
    vec4 color = vColor;

    if (vMode == MODE_SHADOW)
    {
        float antialiasWidth = max(fwidth(outerDistance), 0.0001);
        float outside = smoothstep(-antialiasWidth, antialiasWidth, outerDistance);
        float falloff = clamp(1.0 - max(outerDistance, 0.0) / vShadowDistance, 0.0, 1.0);
        alpha = outside * falloff * falloff;
    }
    else
    {
        alpha = coverage(outerDistance);
        if (vMode == MODE_BORDER)
        {
            vec2 innerOffset = vec2(vThickness.w, vThickness.x);
            vec2 innerSize = vSize - vec2(
                vThickness.w + vThickness.y,
                vThickness.x + vThickness.z);
            float innerDistance = roundedRectDistance(
                vLocalPosition - innerOffset,
                innerSize,
                vInnerRadii);
            alpha *= 1.0 - coverage(innerDistance);
        }
        else if (vMode == MODE_GRADIENT)
        {
            float position = uGradientDirection == 0
                ? vLocalPosition.x / vSize.x
                : vLocalPosition.y / vSize.y;
            if (uGradientOffset != 0.0)
            {
                position = fract(position + uGradientOffset);
            }
            color = gradientColor(clamp(position, 0.0, 1.0));
        }
    }

    FragColor = vec4(color.rgb, color.a * alpha);
}

#version 330 core
in vec2 vTexCoord;
in vec4 vColor;
uniform sampler2D uTexture;
uniform vec4 uSphere;
uniform vec4 uDrawRect;
uniform vec4 uSourceRect;
uniform bool uHasImage;
uniform float uEmphasis;
uniform float uWarpStrength;
uniform vec2 uSurfaceRotation;
out vec4 FragColor;

const float RIM_BLEED_STRENGTH = 0.18; // Maximum blue tint along thumbnail edges.
const float RIM_BLEED_START = 0.45; // Distance from sphere center, in sphere radii.
const float RIM_BLEED_FULL = 0.95; // Distance where the tint reaches full strength.
const vec3 RIM_BLEED_COLOR = vec3(0.3, 0.65, 1.0);

void main()
{
    float radius = uSphere.z * 0.5;
    vec2 center = uSphere.xy + vec2(radius);
    vec2 p = uDrawRect.xy + vTexCoord * uDrawRect.zw - center;
    float inverseDenominator = 1.0 - uWarpStrength * dot(p, p) / (radius * radius);
    if (inverseDenominator <= 0.0)
    {
        discard;
    }

    vec2 source = center + p * inversesqrt(inverseDenominator);
    if (any(notEqual(uSurfaceRotation, vec2(0.0))))
    {
        float surfaceRadius = radius / sqrt(max(uWarpStrength, 0.000001));
        vec2 projected = p / surfaceRadius;
        float depth = 1.0 - dot(projected, projected);
        if (depth <= 0.0)
        {
            discard;
        }

        vec3 normal = vec3(projected, sqrt(depth));
        float cy = cos(uSurfaceRotation.x);
        float sy = sin(uSurfaceRotation.x);
        float cp = cos(uSurfaceRotation.y);
        float sp = sin(uSurfaceRotation.y);
        float originalY = cp * normal.y + sp * normal.z;
        float intermediateZ = -sp * normal.y + cp * normal.z;
        vec3 original = vec3(cy * normal.x - sy * intermediateZ, originalY,
            sy * normal.x + cy * intermediateZ);
        if (original.z <= 0.000001)
        {
            discard;
        }

        source = center + surfaceRadius * original.xy / original.z;
    }
    vec2 size = uSourceRect.zw;
    vec2 uv = (source - uSourceRect.xy) / size;
    float cornerRadius = min(8.0, min(size.x, size.y) * 0.12);
    vec2 edge = abs((uv - 0.5) * size) - size * 0.5 + cornerRadius;
    float distance = length(max(edge, 0.0)) + min(max(edge.x, edge.y), 0.0) - cornerRadius;
    float aa = max(fwidth(distance), 0.001);
    float coverage = 1.0 - smoothstep(-0.5 * aa, 0.5 * aa, distance);
    if (coverage <= 0.0)
    {
        discard;
    }

    vec4 imageColor = vec4(0.035, 0.055, 0.085, 1.0);
    if (uHasImage)
    {
        imageColor = texture(uTexture, clamp(uv, 0.0, 1.0));
    }

    imageColor *= vColor;
    float centerLight = exp(-dot(p, p) / (radius * radius) * 3.5);
    float bleedWidth = max(2.0, min(size.x, size.y) * 0.08);
    float innerGlow = exp(min(distance, 0.0) / bleedWidth) * centerLight * 0.22;
    imageColor.rgb = mix(imageColor.rgb, vec3(1.0, 0.86, 0.5), innerGlow);
    float rimLight = smoothstep(RIM_BLEED_START, RIM_BLEED_FULL, length(p) / radius);
    float rimGlow = exp(min(distance, 0.0) / bleedWidth) * rimLight * RIM_BLEED_STRENGTH;
    imageColor.rgb = mix(imageColor.rgb, RIM_BLEED_COLOR, rimGlow);
    float border = smoothstep(-(1.0 + 1.5 * uEmphasis) * aa, -0.5 * aa, distance) * uEmphasis;
    vec3 borderColor = mix(vec3(0.35, 0.65, 0.9), vec3(0.65, 0.9, 1.0), uEmphasis);
    FragColor = vec4(mix(imageColor.rgb, borderColor, border),
        mix(imageColor.a, vColor.a, border) * coverage);
}

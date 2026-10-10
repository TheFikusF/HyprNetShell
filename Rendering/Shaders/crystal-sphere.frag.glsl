#version 330 core
in vec2 vTexCoord;
in vec4 vColor;
uniform vec4 uSphere;
uniform vec4 uDrawRect;
uniform float uTime;
out vec4 FragColor;

// Sparkle tuning. Colors use RGB values from 0 to 1.
const float SPARKLE_DENSITY = 16.0; // More = more spawn locations across the sphere.
const float EDGE_SPAWN_CHANCE = 0.3; // 0 = never spawn, 1 = spawn every cycle.
const float CENTER_SPAWN_CHANCE = 0.9;
const float SPARKLE_SIZE_PIXELS = 1.2; // Base size before the center boost.
const float SPARKLE_SIZE_VARIATION_PIXELS = 1.6; // Random extra size.
const float CENTER_SIZE_MULTIPLIER = 5.0; // Maximum size boost at the center.
const float CENTER_SIZE_FOCUS = 6.0; // More = concentrate the boost closer to the center.
const float SPARKLE_SPEED = 1.0; // 2 = twice as fast; 0 = freeze the animation.
const float SPARKLE_DRIFT = 0.025; // Travel distance as a fraction of the sphere radius.

// Timing values are fractions of a sparkle's lifetime, from 0 to 1.
const float FADE_IN_END = 0.18;
const float FADE_OUT_START = 0.48;
const float SHRINK_START = 0.55;
const float SHRINK_AMOUNT = 0.75; // 0 = no shrinking, 1 = shrink to nothing.
const float SPARKLE_BRIGHTNESS = 1.0;
const float SPARKLE_HALO_STRENGTH = 0.14;
const float SPARKLE_RAY_STRENGTH = 0.22;
const vec3 SPARKLE_END_COLOR = vec3(1.0, 0.82, 0.32);

// Warm light inside the sphere.
const float INNER_GLOW_STRENGTH = 0.4; // 0 = off; more = stronger yellow glow.
const float INNER_GLOW_FOCUS = 5.0; // More = narrower glow around the center.
const vec3 INNER_GLOW_COLOR = vec3(1.0, 0.85, 0.45);

// Positions and sizes are fractions of the sphere radius; positive Y points down.
const vec2 CLOUD_POSITION = vec2(0.42, 0.43);
const vec2 CLOUD_SIZE = vec2(0.67, 0.3);
const float CLOUD_STRENGTH = 0.48;
const float CLOUD_DETAIL = 8.0; // More = smaller cloud billows.
const float CLOUD_SPEED = 0.018;
const vec3 CLOUD_COLOR = vec3(0.8, 0.87, 0.95);

const vec2 LIGHT_CLOUD_POSITION = vec2(0.55, 0.38);
const vec2 LIGHT_CLOUD_SIZE = vec2(0.43, 0.2);
const float LIGHT_CLOUD_STRENGTH = 0.84;
const float LIGHT_CLOUD_DETAIL = 4.0; // Lower = broader, simpler billows.
const vec3 LIGHT_CLOUD_COLOR = vec3(0.92, 0.95, 1.0);

const vec2 SUN_POSITION = vec2(-0.4, -0.43);
const float SUN_SIZE = 0.065;
const float SUN_STRENGTH = 0.38;
const float SUN_HALO_SIZE = 0.25;
const float SUN_HALO_STRENGTH = 0.25;
const float SUN_RAY_COUNT = 10.0;
const float SUN_RAY_LENGTH = 0.32; // Extent as a fraction of the sphere radius.
const float SUN_OUTWARD_RAY_SCALE = 0.36; // Short rays pointing away from the sphere center.
const float SUN_INWARD_RAY_SCALE = 1.48; // Longer rays pointing toward the sphere center.
const float SUN_RAY_LENGTH_BIAS = 3.0; // Higher = favor short rays; 1 = no extra bias.
const float SUN_RAY_START = 1.2; // Ray fade-in starts this many sun radii from the sun.
const float SUN_RAY_FADE_END = 3.0; // Ray fade-in finishes here; larger = softer roots.
const float SUN_RAY_STRENGTH = 0.3;
const float SUN_RAY_SHARPNESS = 12.0; // Higher = thinner rays.
const vec3 SUN_COLOR = vec3(1.0, 0.88, 0.55);

vec2 SparkleHash(vec2 seed)
{
    return fract(sin(vec2(dot(seed, vec2(127.1, 311.7)), dot(seed, vec2(269.5, 183.3)))) * 43758.5453);
}

float CloudNoise(vec2 p)
{
    vec2 cell = floor(p);
    vec2 blend = fract(p);
    blend = blend * blend * (3.0 - 2.0 * blend);
    return mix(mix(SparkleHash(cell).x, SparkleHash(cell + vec2(1.0, 0.0)).x, blend.x),
        mix(SparkleHash(cell + vec2(0.0, 1.0)).x, SparkleHash(cell + vec2(1.0)).x, blend.x), blend.y);
}

float CloudLayers(vec2 p)
{
    float value = 0.0;
    float weight = 0.5;
    for (int octave = 0; octave < 4; octave++)
    {
        value += CloudNoise(p) * weight;
        p = mat2(0.8, -0.6, 0.6, 0.8) * p * 2.03 + vec2(17.1, 9.2);
        weight *= 0.5;
    }

    return value;
}

vec4 Sparkles(vec2 p, float radius)
{
    vec3 light = vec3(0.0);
    float opacity = 0.0;
    vec2 cell = floor(p * SPARKLE_DENSITY);
    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            vec2 seed = cell + vec2(x, y);
            vec2 random = SparkleHash(seed);
            float clock = uTime * SPARKLE_SPEED * (0.16 + random.x * 0.12) + random.y * 7.0;
            float cycle = floor(clock);
            float age = fract(clock);
            vec2 variation = SparkleHash(seed + cycle * 19.7);
            vec2 center = (seed + 0.25 + variation * 0.5) / SPARKLE_DENSITY;
            center += vec2(sin(age * 2.5 + random.x * 6.283), -age) * SPARKLE_DRIFT;
            float envelope = smoothstep(0.0, FADE_IN_END, age) * (1.0 - smoothstep(FADE_OUT_START, 1.0, age));
            envelope *= 1.0 - smoothstep(0.82, 1.0, length(center));
            float spawnChance = mix(EDGE_SPAWN_CHANCE, CENTER_SPAWN_CHANCE, 1.0 - smoothstep(0.0, 1.0, length(center)));
            float spawnRoll = SparkleHash(seed + cycle * 31.3 + vec2(71.9, 43.7)).x;
            envelope *= 1.0 - step(spawnChance, spawnRoll);
            vec2 delta = p - center;
            float centerWeight = pow(clamp(1.0 - length(center), 0.0, 1.0), CENTER_SIZE_FOCUS);
            float centerScale = mix(1.0, CENTER_SIZE_MULTIPLIER, centerWeight);
            float dissolveScale = 1.0 - smoothstep(SHRINK_START, 1.0, age) * SHRINK_AMOUNT;
            float size = max((SPARKLE_SIZE_PIXELS + variation.x * SPARKLE_SIZE_VARIATION_PIXELS) / radius, 0.0015) * centerScale * dissolveScale;
            float distance = length(delta) / size;
            float core = exp(-distance * distance * 1.8);
            float halo = exp(-distance * distance * 0.12) * SPARKLE_HALO_STRENGTH;
            float rays = exp(-abs(delta.x) / size * 5.0 - abs(delta.y) / size * 0.65)
                + exp(-abs(delta.y) / size * 5.0 - abs(delta.x) / size * 0.65);
            float sparkle = (core + halo + rays * SPARKLE_RAY_STRENGTH) * envelope * SPARKLE_BRIGHTNESS;
            vec3 tint = mix(vec3(1.0), SPARKLE_END_COLOR, smoothstep(0.15, 0.85, age));
            light += tint * sparkle;
            opacity += sparkle;
        }
    }

    return vec4(light, opacity);
}

void main()
{
    float radius = uSphere.z * 0.5;
    vec2 p = (uDrawRect.xy + vTexCoord * uDrawRect.zw - uSphere.xy - vec2(radius)) / radius;
    float radial = length(p);
    float aa = max(fwidth(radial), 0.0001);
    float body = 1.0 - smoothstep(1.0 - aa, 1.0 + aa, radial);
    float depth = sqrt(max(0.0, 1.0 - dot(p, p)));

    float rim = pow(clamp(radial, 0.0, 1.0), 14.0) * body;
    float highlight = pow(max(0.0, dot(vec3(p, depth), normalize(vec3(-0.45, -0.65, 0.8)))), 24.0) * body;
    float glowWidth = max(0.28, 4.0 / radius);
    float glow = 1.0 - smoothstep(0.0, glowWidth, max(0.0, radial - 1.0));
    glow *= (1.0 - body) * exp(-max(0.0, radial - 1.0) * 10.0);
    vec3 crystal = vec3(0.08, 0.22, 0.42);
    vec3 color = crystal + rim * vec3(0.25, 0.48, 0.65) + highlight * vec3(0.45, 0.55, 0.65);
    color = mix(color, vec3(0.22, 0.55, 0.85), glow);
    float alpha = body * (0.12 + 0.13 * depth) + rim * 0.35 + highlight * 0.18 + glow * 0.16;
    float innerGlow = exp(-dot(p, p) * INNER_GLOW_FOCUS) * body * INNER_GLOW_STRENGTH;
    float glowingAlpha = innerGlow + alpha * (1.0 - innerGlow);
    color = (color * alpha * (1.0 - innerGlow) + INNER_GLOW_COLOR * innerGlow)
        / max(glowingAlpha, 0.0001);
    alpha = glowingAlpha;

    vec2 cloudOffset = (p - CLOUD_POSITION) / CLOUD_SIZE;
    vec2 cloudFlow = p * CLOUD_DETAIL + vec2(-uTime * CLOUD_SPEED, uTime * CLOUD_SPEED * 0.3);
    float cloudNoise = CloudLayers(cloudFlow);
    float clouds = smoothstep(0.22, 0.58, cloudNoise)
        * exp(-dot(cloudOffset, cloudOffset) * 0.9) * CLOUD_STRENGTH * body;
    vec3 cloudColor = CLOUD_COLOR * mix(0.65, 1.0, cloudNoise);
    float cloudAlpha = clouds + alpha * (1.0 - clouds);
    color = (color * alpha * (1.0 - clouds) + cloudColor * clouds) / max(cloudAlpha, 0.0001);
    alpha = cloudAlpha;

    vec2 lightCloudOffset = (p - LIGHT_CLOUD_POSITION) / LIGHT_CLOUD_SIZE;
    vec2 lightCloudFlow = p * LIGHT_CLOUD_DETAIL + vec2(13.7, 5.3)
        + vec2(-uTime * CLOUD_SPEED * 0.7, uTime * CLOUD_SPEED * 0.2);
    float lightCloudNoise = CloudNoise(lightCloudFlow) * 0.67
        + CloudNoise(lightCloudFlow * 2.03 + vec2(8.1, 17.2)) * 0.33;
    float lightClouds = smoothstep(0.25, 0.65, lightCloudNoise)
        * exp(-dot(lightCloudOffset, lightCloudOffset) * 0.9) * LIGHT_CLOUD_STRENGTH * body;
    vec3 lightCloudColor = LIGHT_CLOUD_COLOR * mix(0.85, 1.0, lightCloudNoise);
    float lightCloudAlpha = lightClouds + alpha * (1.0 - lightClouds);
    color = (color * alpha * (1.0 - lightClouds) + lightCloudColor * lightClouds)
        / max(lightCloudAlpha, 0.0001);
    alpha = lightCloudAlpha;

    vec2 sunOffset = p - SUN_POSITION;
    float sunDistance = length(sunOffset);
    float sunCore = exp(-sunDistance * sunDistance / (SUN_SIZE * SUN_SIZE)) * SUN_STRENGTH;
    float sunHalo = exp(-sunDistance * sunDistance / (SUN_HALO_SIZE * SUN_HALO_SIZE)) * SUN_HALO_STRENGTH;
    float sunAngle = atan(sunOffset.y, sunOffset.x + 0.000001);
    float rayShape = pow(0.5 + 0.5 * cos(sunAngle * SUN_RAY_COUNT), SUN_RAY_SHARPNESS);
    vec2 rayDirection = sunOffset / max(sunDistance, 0.000001);
    vec2 inwardDirection = -SUN_POSITION / max(length(SUN_POSITION), 0.000001);
    float inwardWeight = smoothstep(-1.0, 1.0, dot(rayDirection, inwardDirection));
    float rayLength = SUN_RAY_LENGTH * mix(SUN_OUTWARD_RAY_SCALE, SUN_INWARD_RAY_SCALE,
        pow(inwardWeight, SUN_RAY_LENGTH_BIAS));
    float rayRoot = smoothstep(SUN_SIZE * SUN_RAY_START, SUN_SIZE * SUN_RAY_FADE_END, sunDistance);
    float sunRays = rayShape * exp(-sunDistance * sunDistance / (rayLength * rayLength))
        * rayRoot * SUN_RAY_STRENGTH;
    float sunlight = clamp(sunCore + sunHalo + sunRays, 0.0, 1.0) * body;
    vec3 sunColor = mix(SUN_COLOR, vec3(1.0, 0.98, 0.88), sunCore);
    float sunAlpha = sunlight + alpha * (1.0 - sunlight);
    color = (color * alpha * (1.0 - sunlight) + sunColor * sunlight) / max(sunAlpha, 0.0001);
    alpha = sunAlpha;

    vec4 sparkles = Sparkles(p, radius) * body;
    float sparkleAlpha = clamp(sparkles.a, 0.0, 1.0);
    float combinedAlpha = sparkleAlpha + alpha * (1.0 - sparkleAlpha);
    color = (color * alpha * (1.0 - sparkleAlpha) + sparkles.rgb)
        / max(combinedAlpha, 0.0001);
    FragColor = vec4(color, clamp(combinedAlpha, 0.0, 1.0) * vColor.a);
}

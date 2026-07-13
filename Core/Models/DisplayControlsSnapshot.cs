namespace HyprNetShell.Core.Models;

public sealed record BacklightSnapshot(
    string DevicePath,
    string Name,
    int Value,
    int Maximum)
{
    public int Percentage => Maximum <= 0
        ? 0
        : Math.Clamp((int)Math.Round(Value * 100.0 / Maximum), 0, 100);
}

public sealed record DisplayControlsSnapshot(
    BacklightSnapshot? Display,
    BacklightSnapshot? Keyboard,
    bool HyprsunsetInstalled,
    bool HyprsunsetRunning,
    int TemperatureKelvin,
    IReadOnlyList<TemperatureCurvePoint> TemperatureCurve,
    bool AutomaticTemperatureEnabled)
{
    public bool Available => Display is not null || Keyboard is not null || HyprsunsetInstalled;

    public static DisplayControlsSnapshot Empty { get; } = new(
        null,
        null,
        false,
        false,
        6000,
        TemperatureCurveMath.DefaultPoints,
        true);
}

public sealed record TemperatureCurvePoint(float Hour, int TemperatureKelvin);

public static class TemperatureCurveMath
{
    public const int MinimumTemperature = 2000;
    public const int MaximumTemperature = 6500;

    public static IReadOnlyList<TemperatureCurvePoint> DefaultPoints { get; } =
    [
        new(0.0f, 3500),
        new(7.0f, 6000),
        new(18.0f, 6000),
        new(24.0f, 3500),
    ];

    public static int Evaluate(IReadOnlyList<TemperatureCurvePoint> points, float hour)
    {
        if (points.Count == 0)
        {
            return 6000;
        }

        hour = Math.Clamp(hour, 0.0f, 24.0f);
        if (hour < points[0].Hour)
        {
            return Interpolate(
                points[^1] with { Hour = points[^1].Hour - 24.0f },
                points[0],
                hour);
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            var left = points[i];
            var right = points[i + 1];
            if (hour > right.Hour)
            {
                continue;
            }

            return Interpolate(left, right, hour);
        }

        return Interpolate(
            points[^1],
            points[0] with { Hour = points[0].Hour + 24.0f },
            hour);
    }

    private static int Interpolate(
        TemperatureCurvePoint left,
        TemperatureCurvePoint right,
        float hour)
    {
        var duration = Math.Max(0.001f, right.Hour - left.Hour);
        var t = Math.Clamp((hour - left.Hour) / duration, 0.0f, 1.0f);
        var eased = t * t * (3.0f - 2.0f * t);
        return (int)MathF.Round(left.TemperatureKelvin +
                                (right.TemperatureKelvin - left.TemperatureKelvin) * eased);
    }
}

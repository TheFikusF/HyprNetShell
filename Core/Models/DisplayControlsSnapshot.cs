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
    bool AutomaticTemperatureEnabled,
    IReadOnlyList<BrightnessCurvePoint> BrightnessCurve,
    bool AutomaticBrightnessEnabled)
{
    public bool Available => Display is not null || Keyboard is not null || HyprsunsetInstalled;

    public static DisplayControlsSnapshot Empty { get; } = new(
        null,
        null,
        false,
        false,
        6000,
        TemperatureCurveMath.DefaultPoints,
        true,
        BrightnessCurveMath.DefaultPoints,
        false);
}

public sealed record TemperatureCurvePoint(float Hour, int TemperatureKelvin);

public sealed record BrightnessCurvePoint(float Hour, int Percentage);

public static class TemperatureCurveMath
{
    public const int MINIMUM_TEMPERATURE = 2000;
    public const int MAXIMUM_TEMPERATURE = 6500;

    public static IReadOnlyList<TemperatureCurvePoint> DefaultPoints { get; } =
    [
        new(0.0f, 3500),
        new(7.0f, 6000),
        new(18.0f, 6000),
        new(24.0f, 3500),
    ];

    public static int Evaluate(IReadOnlyList<TemperatureCurvePoint> points, float hour) =>
        TimeCurveMath.Evaluate(points, hour, point => point.Hour, point => point.TemperatureKelvin, 6000);
}

public static class BrightnessCurveMath
{
    public const int MINIMUM_BRIGHTNESS = 1;
    public const int MAXIMUM_BRIGHTNESS = 100;

    public static IReadOnlyList<BrightnessCurvePoint> DefaultPoints { get; } =
    [
        new(0.0f, 30),
        new(7.0f, 55),
        new(12.0f, 100),
        new(20.0f, 45),
    ];

    public static int Evaluate(IReadOnlyList<BrightnessCurvePoint> points, float hour) =>
        TimeCurveMath.Evaluate(points, hour, point => point.Hour, point => point.Percentage, 50);
}

internal static class TimeCurveMath
{
    internal static int Evaluate<T>(
        IReadOnlyList<T> points,
        float hour,
        Func<T, float> getHour,
        Func<T, int> getValue,
        int fallback)
    {
        if (points.Count == 0)
        {
            return fallback;
        }

        hour = Math.Clamp(hour, 0.0f, 24.0f);
        if (hour < getHour(points[0]))
        {
            return Interpolate(
                getHour(points[^1]) - 24.0f,
                getValue(points[^1]),
                getHour(points[0]),
                getValue(points[0]),
                hour);
        }

        for (var i = 0; i < points.Count - 1; i++)
        {
            if (hour <= getHour(points[i + 1]))
            {
                return Interpolate(
                    getHour(points[i]),
                    getValue(points[i]),
                    getHour(points[i + 1]),
                    getValue(points[i + 1]),
                    hour);
            }
        }

        return Interpolate(
            getHour(points[^1]),
            getValue(points[^1]),
            getHour(points[0]) + 24.0f,
            getValue(points[0]),
            hour);
    }

    private static int Interpolate(float leftHour, int leftValue, float rightHour, int rightValue, float hour)
    {
        var duration = Math.Max(0.001f, rightHour - leftHour);
        var t = Math.Clamp((hour - leftHour) / duration, 0.0f, 1.0f);
        var eased = t * t * (3.0f - 2.0f * t);
        return (int)MathF.Round(leftValue + (rightValue - leftValue) * eased);
    }
}

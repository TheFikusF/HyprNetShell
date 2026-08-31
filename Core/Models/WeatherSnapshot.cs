namespace HyprNetShell.Core.Models;

internal sealed record WeatherSnapshot(
    double? CurrentTemperature,
    int CurrentWeatherCode,
    IReadOnlyList<ForecastDay> Forecast,
    IReadOnlyList<HourlyForecast> Hourly,
    DateTime? UpdatedAt,
    string? Error)
{
    internal static WeatherSnapshot Empty { get; } = new(null, 0, [], [], null, null);
}

internal sealed record ForecastDay(DateOnly Date, double Minimum, double Maximum, int WeatherCode);

internal sealed record HourlyForecast(
    DateTime Time,
    double Temperature,
    int WeatherCode,
    int PrecipitationProbability);

internal readonly record struct WeatherCondition(string Icon, string Description);

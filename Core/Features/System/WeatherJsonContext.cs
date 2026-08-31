using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Features.System;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WeatherResponse))]
internal sealed partial class WeatherJsonContext : JsonSerializerContext
{
}

internal sealed class WeatherResponse
{
    [JsonPropertyName("current")] public CurrentWeather? Current { get; init; }

    [JsonPropertyName("daily")] public DailyWeather? Daily { get; init; }

    [JsonPropertyName("hourly")] public HourlyWeather? Hourly { get; init; }
}

internal sealed class CurrentWeather
{
    [JsonPropertyName("temperature_2m")] public double Temperature { get; init; }

    [JsonPropertyName("weather_code")] public int WeatherCode { get; init; }
}

internal sealed class HourlyWeather
{
    [JsonPropertyName("time")] public List<string> Time { get; init; } = [];

    [JsonPropertyName("temperature_2m")] public List<double?> Temperature { get; init; } = [];

    [JsonPropertyName("weather_code")] public List<int?> WeatherCode { get; init; } = [];

    [JsonPropertyName("precipitation_probability")] public List<int?> PrecipitationProbability { get; init; } = [];
}

internal sealed class DailyWeather
{
    [JsonPropertyName("time")] public List<string> Time { get; init; } = [];

    [JsonPropertyName("weather_code")] public List<int> WeatherCode { get; init; } = [];

    [JsonPropertyName("temperature_2m_max")]
    public List<double> MaximumTemperature { get; init; } = [];

    [JsonPropertyName("temperature_2m_min")]
    public List<double> MinimumTemperature { get; init; } = [];
}

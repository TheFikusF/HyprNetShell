using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WeatherResponse))]
internal sealed partial class WeatherJsonContext : JsonSerializerContext
{
}

internal sealed class WeatherResponse
{
    [JsonPropertyName("current")] public CurrentWeather? Current { get; init; }

    [JsonPropertyName("daily")] public DailyWeather? Daily { get; init; }
}

internal sealed class CurrentWeather
{
    [JsonPropertyName("temperature_2m")] public double Temperature { get; init; }

    [JsonPropertyName("weather_code")] public int WeatherCode { get; init; }
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

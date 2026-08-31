using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

internal sealed class WeatherService : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly string _browserUrl;

    private WeatherSnapshot _snapshot = WeatherSnapshot.Empty;
    private DateTime _nextRefresh = DateTime.MinValue;
    private Task? _refreshTask;
    private bool _disposed;

    internal WeatherService()
    {
        _latitude = ReadCoordinate("HYPRNETSHELL_WEATHER_LATITUDE", 49.195278);
        _longitude = ReadCoordinate("HYPRNETSHELL_WEATHER_LONGITUDE", 16.608333);
        Location = Environment.GetEnvironmentVariable("HYPRNETSHELL_WEATHER_LOCATION")?.Trim() is { Length: > 0 } name
            ? name
            : "Brno";
        _browserUrl = Environment.GetEnvironmentVariable("HYPRNETSHELL_WEATHER_URL")?.Trim() is { Length: > 0 } url
            ? url
            : $"https://www.google.com/search?q={Uri.EscapeDataString("weather " + Location)}";
    }

    internal string Location { get; }

    internal WeatherSnapshot Snapshot
    {
        get
        {
            EnsureRefresh();
            lock (_stateLock)
            {
                return _snapshot;
            }
        }
    }

    internal bool IsRefreshing
    {
        get
        {
            lock (_stateLock)
            {
                return _refreshTask is { IsCompleted: false };
            }
        }
    }

    internal WeatherCondition GetCondition(int code) => code switch
    {
        0 => new("☀️", "Clear"),
        1 => new("🌤️", "Mostly clear"),
        2 => new("⛅", "Partly cloudy"),
        3 => new("☁️", "Overcast"),
        45 or 48 => new("🌫️", "Fog"),
        >= 51 and <= 57 => new("🌦️", "Drizzle"),
        >= 61 and <= 67 => new("🌧️", "Rain"),
        >= 71 and <= 77 => new("🌨️", "Snow"),
        >= 80 and <= 82 => new("🌦️", "Showers"),
        85 or 86 => new("🌨️", "Snow showers"),
        >= 95 => new("⛈️", "Thunderstorm"),
        _ => new("🌡️", "Weather"),
    };

    internal void OpenInBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _browserUrl,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser integration is optional.
        }
    }

    private void EnsureRefresh()
    {
        lock (_stateLock)
        {
            if (_disposed || _refreshTask is { IsCompleted: false } || DateTime.UtcNow < _nextRefresh)
            {
                return;
            }

            _nextRefresh = DateTime.UtcNow + RefreshInterval;
            _refreshTask = RefreshAsync(_lifetime.Token);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await Http.GetStreamAsync(BuildApiUri(), cancellationToken);
            var response = await JsonSerializer.DeserializeAsync(
                               stream,
                               WeatherJsonContext.Default.WeatherResponse,
                               cancellationToken)
                           ?? throw new InvalidDataException("Weather response was empty.");
            var snapshot = ParseResponse(response);

            lock (_stateLock)
            {
                _snapshot = snapshot;
                _nextRefresh = DateTime.UtcNow + RefreshInterval;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Weather", "Could not refresh weather forecast", exception);
            lock (_stateLock)
            {
                if (_snapshot.Forecast.Count == 0)
                {
                    _snapshot = _snapshot with { Error = "Forecast unavailable" };
                }

                _nextRefresh = DateTime.UtcNow + FailureRetryInterval;
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _refreshTask = null;
            }
        }
    }

    private Uri BuildApiUri()
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
            "&current=temperature_2m,weather_code" +
            "&hourly=temperature_2m,weather_code,precipitation_probability" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min" +
            "&temperature_unit=celsius&timezone=auto&forecast_days=7",
            _latitude,
            _longitude);
        return new Uri(url);
    }

    private static WeatherSnapshot ParseResponse(WeatherResponse response)
    {
        var daily = response.Daily ?? throw new InvalidDataException("Daily forecast is missing.");
        var count = new[]
        {
            daily.Time.Count,
            daily.WeatherCode.Count,
            daily.MaximumTemperature.Count,
            daily.MinimumTemperature.Count,
        }.Min();
        var forecast = new List<ForecastDay>(Math.Min(count, 7));
        for (var i = 0; i < count && forecast.Count < 7; i++)
        {
            if (DateOnly.TryParse(daily.Time[i], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                forecast.Add(new ForecastDay(
                    date,
                    daily.MinimumTemperature[i],
                    daily.MaximumTemperature[i],
                    daily.WeatherCode[i]));
            }
        }

        if (forecast.Count == 0)
        {
            throw new InvalidDataException("Daily forecast is empty.");
        }

        var hourlySamples = new List<HourlyForecast>();
        if (response.Hourly is { } hourlyResponse)
        {
            var hourlyCount = new[]
            {
                hourlyResponse.Time.Count,
                hourlyResponse.Temperature.Count,
                hourlyResponse.WeatherCode.Count,
                hourlyResponse.PrecipitationProbability.Count,
            }.Min();
            for (var i = 0; i < hourlyCount; i++)
            {
                if (hourlyResponse.Temperature[i] is not { } temperature ||
                    hourlyResponse.WeatherCode[i] is not { } weatherCode ||
                    !DateTime.TryParse(
                        hourlyResponse.Time[i],
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal,
                        out var time))
                {
                    continue;
                }

                hourlySamples.Add(new HourlyForecast(
                    time,
                    temperature,
                    weatherCode,
                    hourlyResponse.PrecipitationProbability[i] ?? 0));
            }
        }

        var hourly = hourlySamples
            .GroupBy(sample => (Date: DateOnly.FromDateTime(sample.Time), Bucket: sample.Time.Hour / 3))
            .Select(bucket =>
            {
                var first = bucket.MinBy(sample => sample.Time)!;
                return first with
                {
                    Temperature = bucket.Average(sample => sample.Temperature),
                    PrecipitationProbability = bucket.Max(sample => sample.PrecipitationProbability),
                };
            })
            .OrderBy(sample => sample.Time)
            .ToArray();

        return new WeatherSnapshot(
            response.Current?.Temperature,
            response.Current?.WeatherCode ?? forecast[0].WeatherCode,
            forecast,
            hourly,
            DateTime.Now,
            null);
    }

    private static double ReadCoordinate(string variable, double fallback) =>
        double.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}

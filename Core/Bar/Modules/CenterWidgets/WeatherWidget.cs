using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class WeatherWidget
{
    public const int WIDTH = 260;

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailureRetryInterval = TimeSpan.FromMinutes(10);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private readonly Lock _stateLock = new();
    private readonly ModulesCommon.BoxState _titleState = new();
    private readonly Theme _theme;
    private readonly double _latitude;
    private readonly double _longitude;
    private readonly string _location;
    private readonly string _browserUrl;

    private WeatherState _state = WeatherState.Empty;
    private DateTime _nextRefresh = DateTime.MinValue;
    private Task? _refreshTask;

    public WeatherWidget(Theme theme)
    {
        _theme = theme;
        _latitude = ReadCoordinate("HYPRNETSHELL_WEATHER_LATITUDE", 49.195278);
        _longitude = ReadCoordinate("HYPRNETSHELL_WEATHER_LONGITUDE", 16.608333);
        _location = Environment.GetEnvironmentVariable("HYPRNETSHELL_WEATHER_LOCATION")?.Trim() is { Length: > 0 } name
            ? name
            : "Brno";
        _browserUrl = Environment.GetEnvironmentVariable("HYPRNETSHELL_WEATHER_URL")?.Trim() is { Length: > 0 } url
            ? url
            : $"https://www.google.com/search?q={Uri.EscapeDataString("weather " + _location)}";
    }

    internal string Location => _location;

    internal WeatherState Snapshot
    {
        get
        {
            EnsureRefresh();
            lock (_stateLock)
            {
                return _state;
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

    public Node Draw(Action openExpanded)
    {
        EnsureRefresh();

        WeatherState state;
        bool refreshing;
        lock (_stateLock)
        {
            state = _state;
            refreshing = _refreshTask is { IsCompleted: false };
        }

        return new BoxNode(WIDTH)
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Start,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(_theme, _theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 7,
            },
            Children =
            [
                BuildTitleButton(openExpanded),
                new BoxNode(Style.Spacer, ItemsAlignment.End, ItemsAlignment.Center)
                {
                    new TextNode(_location, _theme.TextSize, _theme.Muted),
                    new TextNode(
                        refreshing ? "Updating…" :
                        state.UpdatedAt is null ? "Weather" : state.UpdatedAt.Value.ToString("HH:mm"),
                        _theme.TextSize,
                        _theme.Muted),
                },
                ..BuildWeatherContent(state, refreshing),
            ],
        };
    }

    private Node BuildTitleButton(Action openExpanded)
    {
        var state = _titleState.UpdateColor(_theme.Panel);
        return new BoxNode(height: 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = openExpanded,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(_theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 7,
            },
            Children =
            [
                new ImageNode(Icons.CloudSun, 20, 20, _theme.Text),
                new TextNode("Weather", 20, _theme.Text),
            ],
        };
    }

    private IEnumerable<Node> BuildWeatherContent(WeatherState state, bool refreshing)
    {
        if (state.Forecast.Count == 0)
        {
            yield return new TextNode(
                refreshing ? "Loading forecast…" : state.Error ?? "Weather unavailable",
                _theme.TextSize,
                _theme.Muted);
            yield break;
        }

        var currentCondition = Condition(state.CurrentWeatherCode);
        yield return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Padding = new Insets(0, 12) },
            Children =
            [
                new TextNode($"{currentCondition.Icon} {currentCondition.Description}", 18, _theme.Text),
                new TextNode(
                    state.CurrentTemperature is { } temperature ? $"{Math.Round(temperature):0}°C" : "--°C",
                    22,
                    _theme.Text),
            ],
        };

        var overallMinimum = state.Forecast.Min(day => day.Minimum);
        var overallMaximum = state.Forecast.Max(day => day.Maximum);
        foreach (var day in state.Forecast.Take(7))
        {
            yield return BuildForecastRow(day, overallMinimum, overallMaximum);
        }

        yield return new TextNode("Forecast: Open-Meteo", 14, _theme.Muted);
    }

    private Node BuildForecastRow(ForecastDay day, double overallMinimum, double overallMaximum)
    {
        var condition = Condition(day.WeatherCode);
        var label = day.Date == DateOnly.FromDateTime(DateTime.Today) ? $"> {day.Date:ddd}" : $"  {day.Date:ddd}";
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Children =
            [
                new TextNode(label, _theme.TextSize, _theme.Text),
                new TextNode(condition.Icon, _theme.TextSize, _theme.Text),
                new TextNode($"{Math.Round(day.Minimum):0}°", _theme.TextSize, _theme.Muted),
                new TemperatureRangeNode(
                    day.Minimum,
                    day.Maximum,
                    overallMinimum,
                    overallMaximum,
                    _theme),
                new TextNode($"{Math.Round(day.Maximum):0}°", _theme.TextSize, _theme.Text),
            ],
        };
    }

    private void EnsureRefresh()
    {
        lock (_stateLock)
        {
            if (_refreshTask is { IsCompleted: false } || DateTime.UtcNow < _nextRefresh)
            {
                return;
            }

            _nextRefresh = DateTime.UtcNow + RefreshInterval;
            _refreshTask = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var uri = BuildApiUri();
            await using var stream = await Http.GetStreamAsync(uri);
            var response = await JsonSerializer.DeserializeAsync(
                               stream,
                               WeatherJsonContext.Default.WeatherResponse)
                           ?? throw new InvalidDataException("Weather response was empty.");
            var state = ParseResponse(response);

            lock (_stateLock)
            {
                _state = state;
                _nextRefresh = DateTime.UtcNow + RefreshInterval;
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Weather", "Could not refresh weather forecast", exception);
            lock (_stateLock)
            {
                if (_state.Forecast.Count == 0)
                {
                    _state = _state with { Error = "Forecast unavailable" };
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

    private static WeatherState ParseResponse(WeatherResponse response)
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

        var hourly = new List<HourlyForecast>();
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
                        out var time) ||
                    time.Date != DateTime.Today)
                {
                    continue;
                }

                hourly.Add(new HourlyForecast(
                    time,
                    temperature,
                    weatherCode,
                    hourlyResponse.PrecipitationProbability[i] ?? 0));
            }
        }

        return new WeatherState(
            response.Current?.Temperature,
            response.Current?.WeatherCode ?? forecast[0].WeatherCode,
            forecast,
            hourly,
            DateTime.Now,
            null);
    }

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

    private static double ReadCoordinate(string variable, double fallback) =>
        double.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : fallback;

    internal static (string Icon, string Description) Condition(int code) => code switch
    {
        0 => ("☀️", "Clear"),
        1 => ("🌤️", "Mostly clear"),
        2 => ("⛅", "Partly cloudy"),
        3 => ("☁️", "Overcast"),
        45 or 48 => ("🌫️", "Fog"),
        >= 51 and <= 57 => ("🌦️", "Drizzle"),
        >= 61 and <= 67 => ("🌧️", "Rain"),
        >= 71 and <= 77 => ("🌨️", "Snow"),
        >= 80 and <= 82 => ("🌦️", "Showers"),
        85 or 86 => ("🌨️", "Snow showers"),
        >= 95 => ("⛈️", "Thunderstorm"),
        _ => ("🌡️", "Weather"),
    };

    internal sealed record WeatherState(
        double? CurrentTemperature,
        int CurrentWeatherCode,
        IReadOnlyList<ForecastDay> Forecast,
        IReadOnlyList<HourlyForecast> Hourly,
        DateTime? UpdatedAt,
        string? Error)
    {
        public static WeatherState Empty { get; } = new(null, 0, [], [], null, null);
    }

    internal sealed record ForecastDay(DateOnly Date, double Minimum, double Maximum, int WeatherCode);

    internal sealed record HourlyForecast(DateTime Time, double Temperature, int WeatherCode, int PrecipitationProbability);
}

file sealed class TemperatureRangeNode(
    double minimum,
    double maximum,
    double overallMinimum,
    double overallMaximum,
    Theme theme) : Node
{
    public override int Width => 72;
    public override int Height => 8;

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
        var track = new Rect(x, y, Width, Height);
        renderer.FillRoundedRect(track, Height / 2f, Color.Lighten(theme.Panel, 0.15f));

        var span = Math.Max(1, overallMaximum - overallMinimum);
        var start = (float)((minimum - overallMinimum) / span * Width);
        var end = (float)((maximum - overallMinimum) / span * Width);
        var range = new Rect(x + start, y, Math.Max(3, end - start), Height);
        var temperature = (float)(((minimum + maximum) / 2 - overallMinimum) / span);
        var color = Color.Lerp(Color.Blue, Color.Lerp(Color.Orange, Color.Yellow, 0.3f), temperature);
        renderer.FillRoundedRect(range, Height / 2f, color);
    }
}
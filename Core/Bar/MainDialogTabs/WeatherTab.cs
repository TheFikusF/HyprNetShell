using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Modules.CenterWidgets;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class WeatherTab(WeatherWidget weather, Theme theme) : IMainDialogTab
{
    public string Id => "weather";
    public string Title => "Weather";
    public SvgAsset Icon => Icons.CloudSun;

    public void Activate()
    {
        _ = weather.Snapshot;
    }

    public void HandleTextInput(string text)
    {
    }

    public void HandleBackspace()
    {
    }

    public void MoveSelection(SelectionDirection direction)
    {
    }

    public void ActivateSelection()
    {
    }

    public Node Draw()
    {
        var state = weather.Snapshot;
        if (state.Forecast.Count == 0)
        {
            return new BoxNode
            {
                Direction = Direction.Vertical,
                Style = new Style { Spacing = 12 },
                Children =
                [
                    MainDialogTabUi.BuildSectionHeader("Weather", weather.Location),
                    new TextNode(
                        weather.IsRefreshing ? "Loading forecast…" : state.Error ?? "Weather unavailable",
                        16,
                        theme.Muted),
                ],
            };
        }

        var condition = WeatherWidget.Condition(state.CurrentWeatherCode);
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 14 },
            Children =
            [
                BuildHeader(state),
                new BoxNode
                {
                    HorizontalAlignment = ItemsAlignment.Spread,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
                    {
                        Padding = 18,
                        BorderRadius = 8,
                        BorderWidth = 0,
                    },
                    Children =
                    [
                        new TextNode($"{condition.Icon}  {condition.Description}", 24, theme.Text),
                        new TextNode(
                            state.CurrentTemperature is { } temperature ? $"{Math.Round(temperature):0}°C" : "--°C",
                            34,
                            theme.Text),
                    ],
                },
                MainDialogTabUi.BuildSectionHeader("Today", "Hourly temperature and chance of precipitation"),
                BuildToday(state.Hourly),
                MainDialogTabUi.BuildSectionHeader("7-day forecast", "Daily low, high, and conditions"),
                BuildDaily(state.Forecast),
                new TextNode("Forecast: Open-Meteo", 13, theme.Muted),
            ],
        };
    }

    private Node BuildHeader(WeatherWidget.WeatherState state) => new BoxNode
    {
        HorizontalAlignment = ItemsAlignment.Spread,
        VerticalAlignment = ItemsAlignment.Center,
        Children =
        [
            MainDialogTabUi.BuildSectionHeader(
                $"Weather in {weather.Location}",
                weather.IsRefreshing ? "Updating…" : $"Updated {state.UpdatedAt:HH:mm}"),
            new BoxNode
            {
                OnClick = weather.OpenInBrowser,
                Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
                {
                    BorderRadius = 8,
                    BorderWidth = 0,
                },
                Children = [new TextNode("Open forecast", 14, theme.Text)],
            },
        ],
    };

    private Node BuildToday(IReadOnlyList<WeatherWidget.HourlyForecast> hourly)
    {
        var visible = hourly.Where(item => item.Time.Hour % 3 == 0).Take(8).ToArray();
        if (visible.Length == 0)
        {
            return new TextNode("Hourly forecast unavailable", 14, theme.Muted);
        }

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            Style = new Style { Spacing = 8 },
            Children = [..visible.Select(BuildHour)],
        };
    }

    private Node BuildHour(WeatherWidget.HourlyForecast hour)
    {
        var condition = WeatherWidget.Condition(hour.WeatherCode);
        var current = hour.Time.Hour == DateTime.Now.Hour;
        return new BoxNode(96)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, current ? theme.Active : theme.Panel) with
            {
                Padding = 10,
                BorderRadius = 8,
                BorderWidth = current ? theme.BorderWidth : 0,
                Spacing = 5,
            },
            Children =
            [
                new TextNode(hour.Time.ToString("HH:mm"), 13, theme.Muted),
                new TextNode(condition.Icon, 20, theme.Text),
                new TextNode($"{Math.Round(hour.Temperature):0}°", 17, theme.Text),
                new TextNode($"Rain {hour.PrecipitationProbability}%", 11, theme.Muted),
            ],
        };
    }

    private Node BuildDaily(IReadOnlyList<WeatherWidget.ForecastDay> forecast) => new BoxNode
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Style = new Style { Spacing = 7 },
        Children = [..forecast.Select(BuildDay)],
    };

    private Node BuildDay(WeatherWidget.ForecastDay day)
    {
        var condition = WeatherWidget.Condition(day.WeatherCode);
        var today = day.Date == DateOnly.FromDateTime(DateTime.Today);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, today ? theme.Active : theme.Panel) with
            {
                Padding = new Insets(14, 9),
                BorderRadius = 8,
                BorderWidth = today ? theme.BorderWidth : 0,
            },
            Children =
            [
                new TextNode(today ? "Today" : day.Date.ToString("dddd"), 15, theme.Text),
                new TextNode($"{condition.Icon}  {condition.Description}", 14, theme.Text),
                new TextNode($"{Math.Round(day.Minimum):0}° / {Math.Round(day.Maximum):0}°", 15, theme.Text),
            ],
        };
    }
}

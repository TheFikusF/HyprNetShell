using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class WeatherTab(WeatherService weather, Theme theme) : IMainDialogTab
{
    public string Id => "weather";
    public string Title => "Weather";
    public SvgAsset Icon => Icons.CloudSun;

    private readonly ModulesCommon.BoxState _openButtonState = new();
    private readonly Dictionary<DateOnly, ModulesCommon.BoxState> _dayStates = [];
    private int _selectedDayIndex;

    public void Activate()
    {
        _selectedDayIndex = 0;
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
        var dayCount = weather.Snapshot.Forecast.Count;
        if (dayCount == 0)
        {
            return;
        }

        _selectedDayIndex = direction switch
        {
            SelectionDirection.Up => Math.Max(0, _selectedDayIndex - 1),
            SelectionDirection.Down => Math.Min(dayCount - 1, _selectedDayIndex + 1),
            _ => _selectedDayIndex,
        };
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
                    new TextNode(weather.IsRefreshing ? "Loading forecast…" : state.Error ?? "Weather unavailable",
                        18, theme.Muted),
                ],
            };
        }

        _selectedDayIndex = Math.Min(_selectedDayIndex, state.Forecast.Count - 1);
        var selectedDay = state.Forecast[_selectedDayIndex];
        var condition = weather.GetCondition(state.CurrentWeatherCode);
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 16 },
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
                        new TextNode(state.CurrentTemperature is { } temperature ? $"{Math.Round(temperature):0}°C" : "--°C",
                            34, theme.Text),
                    ],
                },
                MainDialogTabUi.BuildSectionHeader(
                    selectedDay.Date == DateOnly.FromDateTime(DateTime.Today)
                        ? "Today"
                        : selectedDay.Date.ToString("dddd, MMMM d"),
                    "Hourly temperature and chance of precipitation · ↑/↓ to change day"),
                BuildHourly(state.Hourly, selectedDay.Date),
                MainDialogTabUi.BuildSectionHeader("7-day forecast", "Daily low, high, and conditions"),
                BuildDaily(state.Forecast),
                new TextNode("Forecast: Open-Meteo", theme.TextSize, theme.Muted),
            ],
        };
    }

    private BoxNode BuildHeader(WeatherSnapshot state)
    {
        var buttonState = _openButtonState.UpdateColor(theme.Panel);
        return new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
        {
            MainDialogTabUi.BuildSectionHeader(
                $"Weather in {weather.Location}",
                weather.IsRefreshing ? "Updating…" : $"Updated {state.UpdatedAt:HH:mm}"),
            new BoxNode
            {
                OnClick = weather.OpenInBrowser,
                IsHovered = buttonState,
                Style = ModulesCommon.ModuleStyle(theme, buttonState) with
                {
                    BorderRadius = 8,
                    BorderWidth = 0,
                },
                Children = [new TextNode("Open forecast", theme.TextSize, theme.Text)],
            },
        };
    }

    private Node BuildHourly(IReadOnlyList<HourlyForecast> hourly, DateOnly selectedDate)
    {
        var visible = hourly
            .Where(item => DateOnly.FromDateTime(item.Time) == selectedDate)
            .Take(8)
            .ToArray();
        if (visible.Length == 0)
        {
            return new TextNode("Hourly forecast unavailable", theme.TextSize, theme.Muted);
        }

        return new BoxNode(Style.Spacer, ItemsAlignment.Stretch, ItemsAlignment.Stretch)
        {
            Children = [.. visible.Select(BuildHour)],
        };
    }

    private BoxNode BuildHour(HourlyForecast hour)
    {
        var condition = weather.GetCondition(hour.WeatherCode);
        var current = hour.Time.Date == DateTime.Today && hour.Time.Hour >= DateTime.Now.Hour && hour.Time.Hour <= (DateTime.Now.Hour + 2);
        return new ()
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.ModuleStyle(theme, current ? theme.Active : theme.Panel) with
            {
                Padding = 8 + (current ? 0 : (int)theme.BorderWidth),
                BorderRadius = 8,
                BorderWidth = current ? theme.BorderWidth : 0,
                Spacing = 5,
            },
            Children =
            [
                new TextNode(hour.Time.ToString("HH:mm"), theme.TextSize, current ? theme.Text : theme.Muted),
                new TextNode(condition.Icon, 24, theme.Text),
                new TextNode($"{Math.Round(hour.Temperature):0}°", 18, theme.Text),
                new TextNode($"Rain {hour.PrecipitationProbability}%", theme.TextSize, current ? theme.Text : theme.Muted),
            ],
        };
    }

    private BoxNode BuildDaily(IReadOnlyList<ForecastDay> forecast)
    {
        var overallMinimum = forecast.Min(day => day.Minimum);
        var overallMaximum = forecast.Max(day => day.Maximum);
        return new()
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = Style.Spacer,
            Children = [.. forecast.Select((day, index) => BuildDay(day, index, overallMinimum, overallMaximum))],
        };
    }

    private BoxNode BuildDay(ForecastDay day, int index, double overallMinimum, double overallMaximum)
    {
        var condition = weather.GetCondition(day.WeatherCode);
        var today = day.Date == DateOnly.FromDateTime(DateTime.Today);
        var selected = index == _selectedDayIndex;
        var state = _dayStates.GetState(day.Date, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => _selectedDayIndex = index,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = new Insets(14, 9),
                BorderRadius = 8,
                BorderWidth = selected ? theme.BorderWidth : 0,
            },
            Children =
            [
                new TextNode(
                    $"{(selected ? ">" : " ")} {(today ? "Today" : day.Date.ToString("dddd"))}",
                    theme.TextSize,
                    theme.Text),
                new TextNode($"{condition.Icon}  {condition.Description}", theme.TextSize, theme.Text),
                new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                {
                    new TextNode($"{Math.Round(day.Minimum):0}°", theme.TextSize, theme.Text),
                    new WeatherTemperatureRangeNode(
                        day.Minimum,
                        day.Maximum,
                        overallMinimum,
                        overallMaximum,
                        theme),
                    new TextNode($"{Math.Round(day.Maximum):0}°", theme.TextSize, theme.Text),
                }
            ],
        };
    }
}

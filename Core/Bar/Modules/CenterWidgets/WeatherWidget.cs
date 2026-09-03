using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class WeatherWidget(WeatherService weather, Theme theme)
{
    public const int WIDTH = 260;

    private readonly ModulesCommon.BoxState _titleState = new();

    public Node Draw(Action openExpanded)
    {
        var state = weather.Snapshot;
        var refreshing = weather.IsRefreshing;

        return new BoxNode(WIDTH)
        {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Start,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(theme, theme.Panel) with
            {
                BorderRadius = 8,
                Spacing = 7,
            },
            Children =
            [
                BuildTitleButton(openExpanded),
                new BoxNode(Style.Spacer, ItemsAlignment.End, ItemsAlignment.Center)
                {
                    new TextNode(weather.Location, theme.Text, theme.Text.MutedColor),
                    new TextNode(
                        refreshing ? "Updating…" :
                        state.UpdatedAt is null ? "Weather" : state.UpdatedAt.Value.ToString("HH:mm"),
                        theme.Text,
                        theme.Text.MutedColor),
                },
                ..BuildWeatherContent(state, refreshing),
            ],
        };
    }

    private BoxNode BuildTitleButton(Action openExpanded)
    {
        var state = _titleState.UpdateColor(theme.Panel);
        return new BoxNode(height: 34)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = openExpanded,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                Padding = 0,
                BorderRadius = 8,
                BorderWidth = 0,
                Spacing = 8,
            },
            Children =
            [
                new ImageNode(Icons.CloudSun, 20, 20, theme.Text),
                new TextNode("Weather", 20, theme.Text),
            ],
        };
    }

    private IEnumerable<Node> BuildWeatherContent(WeatherSnapshot state, bool refreshing)
    {
        if (state.Forecast.Count == 0)
        {
            yield return new TextNode(
                refreshing ? "Loading forecast…" : state.Error ?? "Weather unavailable",
                theme.Text,
                theme.Text.MutedColor);
            yield break;
        }

        var currentCondition = weather.GetCondition(state.CurrentWeatherCode);
        yield return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Padding = new Insets(0, 12) },
            Children =
            [
                new TextNode($"{currentCondition.Icon} {currentCondition.Description}", 18, theme.Text),
                new TextNode(
                    state.CurrentTemperature is { } temperature ? $"{Math.Round(temperature):0}°C" : "--°C",
                    22,
                    theme.Text),
            ],
        };

        var overallMinimum = state.Forecast.Min(day => day.Minimum);
        var overallMaximum = state.Forecast.Max(day => day.Maximum);
        foreach (var day in state.Forecast.Take(7))
        {
            yield return BuildForecastRow(day, overallMinimum, overallMaximum);
        }

        yield return new TextNode("Forecast: Open-Meteo", theme.Text, theme.Text.MutedColor);
    }

    private Node BuildForecastRow(ForecastDay day, double overallMinimum, double overallMaximum)
    {
        var condition = weather.GetCondition(day.WeatherCode);
        var label = day.Date == DateOnly.FromDateTime(DateTime.Today) ? $"> {day.Date:ddd}" : $"  {day.Date:ddd}";
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Children =
            [
                new TextNode(label, theme.Text, theme.Text),
                new TextNode(condition.Icon, theme.Text, theme.Text),
                new TextNode($"{Math.Round(day.Minimum):0}°", theme.Text, theme.Text.MutedColor),
                new WeatherTemperatureRangeNode(
                    day.Minimum,
                    day.Maximum,
                    overallMinimum,
                    overallMaximum,
                    theme),
                new TextNode($"{Math.Round(day.Maximum):0}°", theme.Text, theme.Text),
            ],
        };
    }
}

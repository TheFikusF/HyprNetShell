using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules.CenterWidgets;

internal sealed class WeatherWidget(WeatherService weather)
{
    public const int WIDTH = 260;

    private readonly ModulesCommon.BoxState _titleState = new();

    public Node Draw(Action openExpanded)
    {
        var state = weather.Snapshot;
        var refreshing = weather.IsRefreshing;

        return new BoxNode(WIDTH) {
            Direction = Direction.Vertical,
            VerticalAlignment = ItemsAlignment.Start,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with {
                BorderRadius = 8,
                Spacing = 8,
            },
            Children =
            [
                ModulesCommon.CentralWidgetHeader(Icons.CloudSun, "Weather", openExpanded, _titleState),
                new BoxNode(Style.Spacer, ItemsAlignment.End, ItemsAlignment.Center)
                {
                    new TextNode(weather.Location, color: ThemeManager.Current.Text.MutedColor),
                    new TextNode(refreshing ? "Updating…" :
                        state.UpdatedAt is null ? "Weather" : state.UpdatedAt.Value.ToString("HH:mm"), color: ThemeManager.Current.Text.MutedColor),
                },
                ..BuildWeatherContent(state, refreshing),
            ],
        };
    }

    private IEnumerable<Node> BuildWeatherContent(WeatherSnapshot state, bool refreshing)
    {
        if (state.Forecast.Count == 0)
        {
            yield return new TextNode(refreshing ? "Loading forecast…" : state.Error ?? "Weather unavailable", color: ThemeManager.Current.Text.MutedColor);
            yield break;
        }

        var currentCondition = weather.GetCondition(state.CurrentWeatherCode);
        yield return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Padding = new Insets(0, 12) },
            Children =
            [
                new TextNode($"{currentCondition.Icon} {currentCondition.Description}", 18),
                new TextNode(state.CurrentTemperature is { } temperature ? $"{Math.Round(temperature):0}°C" : "--°C", 22),
            ],
        };

        var overallMinimum = state.Forecast.Min(day => day.Minimum);
        var overallMaximum = state.Forecast.Max(day => day.Maximum);
        foreach (var day in state.Forecast.Take(7))
        {
            yield return BuildForecastRow(day, overallMinimum, overallMaximum);
        }

        yield return new TextNode("Forecast: Open-Meteo", color: ThemeManager.Current.Text.MutedColor);
    }

    private Node BuildForecastRow(ForecastDay day, double overallMinimum, double overallMaximum)
    {
        var condition = weather.GetCondition(day.WeatherCode);
        var label = day.Date == DateOnly.FromDateTime(DateTime.Today) ? $"> {day.Date:ddd}" : $"  {day.Date:ddd}";
        return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Children =
            [
                new TextNode(label),
                new TextNode(condition.Icon),
                new TextNode($"{Math.Round(day.Minimum):0}°", color: ThemeManager.Current.Text.MutedColor),
                new WeatherTemperatureRangeNode(
                    day.Minimum,
                    day.Maximum,
                    overallMinimum,
                    overallMaximum),
                new TextNode($"{Math.Round(day.Maximum):0}°"),
            ],
        };
    }
}

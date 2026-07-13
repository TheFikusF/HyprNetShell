using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar;

internal sealed class SystemStatsModule(Func<SystemStatsSnapshot> snapshot, StatusBarTheme theme) : IDrawableModule
{
    private const int WIDTH = 75;

    private Color ColorFromCpuPercent(int percent) => percent switch
    {
        > 90 => theme.Critical,
        > 75 => theme.Warning,
        _ => theme.Panel,
    };

    private Color ColorFromRamPercent(int percent) => percent switch
    {
        > 90 => theme.Critical,
        > 70 => theme.Warning,
        _ => theme.Panel,
    };

    private Color ColorFromTempPercent(int temp) => temp switch
    {
        > 90 => theme.Critical,
        > 75 => theme.Warning,
        _ => theme.Panel,
    };

    public Node Draw()
    {
        var stats = snapshot();

        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Children =
            [
                new BoxNode(WIDTH)
                {
                    Direction = Direction.Horizontal,
                    VerticalAlignment = ItemsAlignment.Center,
                    HorizontalAlignment = ItemsAlignment.Center,
                    Style = ModulesCommon.ModuleStyle(
                        theme, ColorFromCpuPercent(stats.CpuPercent ?? 0), right: false) with { Spacing = 6 },
                    Children =
                    [
                        new ImageNode(Icons.Cpu, 17, 17, theme.Text),
                        new TextNode(FormatPercent(stats.CpuPercent), 14.0f, theme.Text),
                    ]
                },
                new BoxNode(WIDTH)
                {
                    Direction = Direction.Horizontal,
                    VerticalAlignment = ItemsAlignment.Center,
                    HorizontalAlignment = ItemsAlignment.Center,
                    Style =
                        ModulesCommon.ModuleStyle(theme, ColorFromRamPercent(stats.RamPercent ?? 0), false, false) with
                        {
                            BorderWidth = new Insets(1, theme.BorderWidth),
                            Spacing = 6,
                        },
                    Children =
                    [
                        new ImageNode(Icons.Memory, 17, 17, theme.Text),
                        new TextNode(FormatPercent(stats.RamPercent), 14.0f, theme.Text),
                    ]
                },
                new BoxNode(WIDTH)
                {
                    Direction = Direction.Horizontal,
                    VerticalAlignment = ItemsAlignment.Center,
                    HorizontalAlignment = ItemsAlignment.Center,
                    Style = ModulesCommon.ModuleStyle(theme, ColorFromTempPercent(stats.TemperatureCelsius ?? 0),
                        left: false) with { Spacing = 6 },
                    Children =
                    [
                        new ImageNode(Icons.Temperature, 17, 17, theme.Text),
                        new TextNode(FormatTemperature(stats.TemperatureCelsius), 14.0f, theme.Text),
                    ]
                }
            ],
        };
    }

    private static string FormatPercent(int? value) => value.HasValue ? $"{value.Value}%" : "?";
    private static string FormatTemperature(int? value) => value.HasValue ? $"{value.Value}C" : "?";
}

using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Models;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class SystemStatsModule(Func<SystemStatsSnapshot> snapshot, Theme theme) : IDrawableModule
{
    private const int WIDTH = 75;

    private readonly Gradient _cpuGradient = new(
        new Gradient.Stop(0, ModulesCommon.ToBackground(theme, Color.Violet)),
        new Gradient.Stop(0.6f, ModulesCommon.ToBackground(theme, Color.Violet)),
        new Gradient.Stop(0.75f, theme.Warning),
        new Gradient.Stop(1f, Color.Red)
    );

    private readonly Gradient _ramGradient = new(
        new Gradient.Stop(0, ModulesCommon.ToBackground(theme, Color.Green)),
        new Gradient.Stop(0.6f, ModulesCommon.ToBackground(theme, Color.Green)),
        new Gradient.Stop(0.70f, theme.Warning),
        new Gradient.Stop(1f, Color.Red)
    );

    private readonly Gradient _tempGradient = new(
        new Gradient.Stop(0, ModulesCommon.ToBackground(theme, Color.Orange)),
        new Gradient.Stop(0.6f, ModulesCommon.ToBackground(theme, Color.Orange)),
        new Gradient.Stop(0.75f, theme.Warning),
        new Gradient.Stop(1f, Color.Red)
    );

    private Color _currentCpuColor;
    private Color _currentRamColor;
    private Color _currentTempColor;

    private void Lerp(ref Color color, Gradient gradient, float percent)
    {
        color = color.LerpSmooth(gradient.Evaluate(percent), 18.0f, ModulesCommon.DELTA_TIME);
    }

    public Node Draw()
    {
        var stats = snapshot();
        
        Lerp(ref _currentCpuColor, _cpuGradient, (float)(stats.CpuPercent ?? 0) / 100);
        Lerp(ref _currentRamColor, _ramGradient, (float)(stats.RamPercent ?? 0) / 100);
        Lerp(ref _currentTempColor, _tempGradient, (float)(stats.TemperatureCelsius ?? 0) / 100);

        return new BoxNode
        {
            Direction = Direction.Horizontal,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Children =
            [
                ModulesCommon.BuildTextWithIcon(theme, Icons.Cpu, FormatPercent(stats.CpuPercent),
                    style: ModulesCommon.ModuleStyle(theme, _currentCpuColor, right: false), width: WIDTH),
                ModulesCommon.BuildTextWithIcon(theme, Icons.Memory, FormatPercent(stats.RamPercent),
                    style: ModulesCommon.ModuleStyle(theme, _currentRamColor, false, false), width: WIDTH),
                ModulesCommon.BuildTextWithIcon(theme, Icons.Temperature, FormatTemperature(stats.TemperatureCelsius),
                    style: ModulesCommon.ModuleStyle(theme, _currentTempColor, left: false), width: WIDTH),
            ],
        };
    }

    private static string FormatPercent(int? value) => value.HasValue ? $"{value.Value}%" : "?";
    private static string FormatTemperature(int? value) => value.HasValue ? $"{value.Value}°C" : "?";
}
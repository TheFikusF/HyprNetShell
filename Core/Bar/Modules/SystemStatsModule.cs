using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Nodes;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.Modules;

internal sealed class SystemStatsModule(SystemStatsModuleService service,
    Theme theme, PopupCoordinator popupCoordinator) : IDrawableModule
{
    private const int WIDTH = 75;
    private const int GRAPH_WIDTH = 400;
    private const int GRAPH_HEIGHT = 92;

    private readonly NodeWithPopup _node = new(popupCoordinator, "system_stats_module")
    {
        HorizontalAlignment = ItemsAlignment.Center,
    };

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
        color = color.LerpSmooth(gradient.Evaluate(percent), 18.0f, Renderer.DeltaTime);
    }

    public Node Draw()
    {
        var stats = service.Snapshot;

        Lerp(ref _currentCpuColor, _cpuGradient, (float)(stats.CpuPercent ?? 0) / 100);
        Lerp(ref _currentRamColor, _ramGradient, (float)(stats.RamPercent ?? 0) / 100);
        Lerp(ref _currentTempColor, _tempGradient, (float)(stats.TemperatureCelsius ?? 0) / 100);

        return _node.Draw([BuildStateModule(stats)], () => BuildPopup(stats));
    }

    private BoxNode BuildStateModule(SystemStatsSnapshot stats) => new()
    {
        Direction = Direction.Horizontal,
        VerticalAlignment = ItemsAlignment.Center,
        HorizontalAlignment = ItemsAlignment.Center,
        Style = new Style()
        {
            BorderRadius = 999,
            ShadowColor = Color.Black with { A = 0.45f },
            ShadowDistance = 5.0f
        },
        Children =
        {
            ModulesCommon.BuildTextWithIcon(theme, Icons.CPU, FormatPercent(stats.CpuPercent),
                style: ModulesCommon.ModuleStyle(theme, _currentCpuColor, right: false) with
                {
                    ShadowColor = null,
                }, width: WIDTH),
            ModulesCommon.BuildTextWithIcon(theme, Icons.RAM, FormatPercent(stats.RamPercent),
                style: ModulesCommon.ModuleStyle(theme, _currentRamColor, false, false) with
                {
                    BorderWidth = new Insets(1, theme.Border.Width),
                    ShadowColor = null,
                }, width: WIDTH),
            ModulesCommon.BuildTextWithIcon(theme, Icons.Temperature, FormatTemperature(stats.TemperatureCelsius),
                style: ModulesCommon.ModuleStyle(theme, _currentTempColor, left: false) with
                {
                    ShadowColor = null,
                }, width: WIDTH),
        },
    };

    private BoxNode BuildPopup(SystemStatsSnapshot stats)
    {
        var cpuColor = Color.FromRgb(190, 100, 255, 0.9f);
        var gpuColor = Color.FromRgb(255, 145, 55, 0.9f);
        var ramColor = Color.FromRgb(55, 210, 135, 0.9f);
        var swapColor = Color.FromRgb(70, 190, 235, 0.9f);
        var downloadColor = Color.FromRgb(65, 175, 255, 0.9f);
        var uploadColor = Color.FromRgb(80, 225, 215, 0.9f);
        var networkMaximum = NetworkScale(stats.DownloadHistory, stats.UploadHistory);

        return new BoxNode()
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.PopupStyle(theme) with { Spacing = 8 },
            Children =
            [
                ModulesCommon.BuildTextWithIcon(theme, Icons.SquareActivity, "System Info"),
                BuildGraphNode(
                    100.0f,
                    stats.CpuHistory,
                    cpuColor,
                    [
                        ModulesCommon.BuildTextWithIcon(theme, Icons.CPU, "CPU"),
                        new TextNode(FormatPercent(stats.CpuPercent), 14, theme.Text)
                    ],
                    stats.GpuHistory,
                    gpuColor,
                    [
                        ModulesCommon.BuildTextWithIcon(theme, Icons.GPU, "GPU"),
                        new TextNode(FormatPercent(stats.GpuPercent), 14, theme.Text)
                    ]),
                BuildGraphNode(
                    100.0f,
                    stats.RamHistory,
                    ramColor,
                    [
                        ModulesCommon.BuildTextWithIcon(theme, Icons.RAM, "RAM"),
                        new TextNode(FormatPercent(stats.RamPercent), 14, theme.Text)
                    ],
                    stats.SwapHistory,
                    swapColor,
                    [
                        ModulesCommon.BuildTextWithIcon(theme, Icons.HardDrive, "Swap"),
                        new TextNode(FormatPercent(stats.SwapPercent), 14, theme.Text)
                    ]),
                BuildGraphNode(
                    networkMaximum,
                    stats.DownloadHistory,
                    downloadColor,
                    [
                        ModulesCommon.BuildTextWithIcon(theme, Icons.ArrowDown, "Download"),
                        new TextNode(FormatRate(stats.DownloadBytesPerSecond), 14, theme.Text)
                    ],
                    stats.UploadHistory,
                    uploadColor,
                    [
                        ModulesCommon.BuildTextWithIcon(theme, Icons.ArrowUp, "Upload"),
                        new TextNode(FormatRate(stats.UploadBytesPerSecond), 14, theme.Text)
                    ]),
                ..BuildDiskSection(stats.Disks),
            ],
        };
    }

    private BoxNode BuildGraphNode(float max, IReadOnlyList<float> upData, Color upColor, ICollection<Node> upLabel,
        IReadOnlyList<float>? downData = null, Color? downColor = null, ICollection<Node>? downLabel = null)
    {
        var graphBackground = theme.Panel;
        var grid = theme.Text.MutedColor with { A = 0.22f };
        return new BoxNode(ModulesCommon.PopupStyle(theme) with { Padding = 0 })
        {
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children =
            [
                new SystemHistoryGraphNode(
                    GRAPH_WIDTH, GRAPH_HEIGHT,
                    upData, downData,
                    max,
                    upColor, downColor ?? upColor,
                    graphBackground, grid),

                new BoxNode(GRAPH_WIDTH)
                {
                    IgnoreLayout = true,
                    Style = new Style { Padding = 8 },
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Children = upLabel
                },
                new BoxNode(GRAPH_WIDTH)
                {
                    IgnoreLayout = true,
                    Style = new Style { Padding = 8 },
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Bottom = 0,
                    Children = downLabel ?? []
                },
            ]
        };
    }

    private IEnumerable<Node> BuildDiskSection(IReadOnlyList<DiskUsageSnapshot> disks)
    {
        if (disks.Count == 0)
        {
            yield break;
        }

        yield return ModulesCommon.BuildDivider(theme.Border, GRAPH_WIDTH, 12);
        yield return new BoxNode(GRAPH_WIDTH)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            Children = [ModulesCommon.BuildTextWithIcon(theme, Icons.HardDrive, "Disks")],
        };

        foreach (var disk in disks)
        {
            yield return BuildDiskRow(disk);
        }
    }

    private BoxNode BuildDiskRow(DiskUsageSnapshot disk)
    {
        const int BAR_HEIGHT = 10;
        var percentage = disk.Percent;
        var fill = percentage switch
        {
            >= 90 => theme.Critical,
            >= 75 => theme.Warning,
            _ => Color.FromRgb(80, 180, 255),
        };

        return new BoxNode(GRAPH_WIDTH)
        {
            Direction = Direction.Vertical,
            Style = new Style { Spacing = 4 },
            Children =
            [
                new BoxNode(GRAPH_WIDTH)
                {
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Children =
                    [
                        new TextNode(disk.Name, 14.0f, theme.Text, maxWidth: GRAPH_WIDTH - 150,
                            wrapping: TextWrapping.Ellipsis),
                        new TextNode($"{FormatBytes(disk.UsedBytes)} / {FormatBytes(disk.TotalBytes)}  {percentage}%",
                            14.0f, theme.Text),
                    ],
                },
                new BoxNode(GRAPH_WIDTH, BAR_HEIGHT)
                {
                    Style = new Style { BackgroundColor = theme.Text.MutedColor with { A = 0.35f }, BorderRadius = 5 },
                    Children =
                    [
                        new BoxNode((int)MathF.Round(GRAPH_WIDTH * percentage / 100.0f), BAR_HEIGHT)
                        {
                            Style = new Style { BackgroundColor = fill, BorderRadius = 5 },
                        },
                    ],
                },
            ],
        };
    }

    private static float NetworkScale(IReadOnlyList<float> download, IReadOnlyList<float> upload)
    {
        var peak = download.Concat(upload).DefaultIfEmpty(0.0f).Max();
        var scale = 64.0f * 1024.0f;
        while (scale < peak && scale < 1024.0f * 1024.0f * 1024.0f)
        {
            scale *= 2.0f;
        }

        return scale;
    }

    private static string FormatPercent(int? value) => value.HasValue ? $"{value.Value}%" : "?";
    private static string FormatTemperature(int? value) => value.HasValue ? $"{value.Value}°C" : "?";

    private static string FormatRate(long bytesPerSecond) => $"{FormatBytes(bytesPerSecond)}/s";

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var display = (double)value;
        while (display >= 1024.0 && unit < units.Length - 1)
        {
            display /= 1024.0;
            unit++;
        }

        return unit == 0 ? $"{display:0} {units[unit]}" : $"{display:0.#} {units[unit]}";
    }
}

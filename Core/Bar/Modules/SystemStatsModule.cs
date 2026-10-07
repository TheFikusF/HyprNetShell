using HyprNetShell.GUI;
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

internal sealed class SystemStatsModule(SystemStatsModuleService service, PopupCoordinator popupCoordinator) : IDrawableModule
{
    private const int WIDTH = 75;
    private const int GRAPH_WIDTH = 400;
    private const int GRAPH_HEIGHT = 92;

    private readonly NodeWithPopup _node = new(popupCoordinator, "system_stats_module") {
        HorizontalAlignment = ItemsAlignment.Center,
    };

    private readonly Gradient _cpuGradient = new(
        new Gradient.Stop(0, ModulesCommon.ToBackground(Color.Violet)),
        new Gradient.Stop(0.6f, ModulesCommon.ToBackground(Color.Violet)),
        new Gradient.Stop(0.75f, ThemeManager.Current.Warning),
        new Gradient.Stop(1f, Color.Red)
    );

    private readonly Gradient _ramGradient = new(
        new Gradient.Stop(0, ModulesCommon.ToBackground(Color.Green)),
        new Gradient.Stop(0.6f, ModulesCommon.ToBackground(Color.Green)),
        new Gradient.Stop(0.70f, ThemeManager.Current.Warning),
        new Gradient.Stop(1f, Color.Red)
    );

    private readonly Gradient _tempGradient = new(
        new Gradient.Stop(0, ModulesCommon.ToBackground(Color.Orange)),
        new Gradient.Stop(0.6f, ModulesCommon.ToBackground(Color.Orange)),
        new Gradient.Stop(0.75f, ThemeManager.Current.Warning),
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

    private BoxNode Module(SvgAsset icon, string text, Color color, bool left, bool right)
    {
        const Direction DIRECTION = Direction.Horizontal;
        var width = DIRECTION == Direction.Horizontal ? WIDTH : WIDTH - 30;
        var radius = DIRECTION == Direction.Horizontal ? ThemeManager.Current.Border.Radius : 12;

        var style = new Style() {
            BackgroundColor = color,
            BorderRadius = new BorderRadius(left ? radius : 0, right ? radius : 0,
                right ? radius : 0, left ? radius : 0),
            BorderWidth = new Insets(ThemeManager.Current.Border.Width, right ? ThemeManager.Current.Border.Width : 0,
                ThemeManager.Current.Border.Width, left ? ThemeManager.Current.Border.Width : 0),

            Spacing = DIRECTION == Direction.Horizontal ? 8 : 2,
            ShadowColor = null,
        };

        if (left == false && right == false)
        {
            style = style with {
                BorderWidth = new Insets(1, ThemeManager.Current.Border.Width)
            };
        }

        return new BoxNode(width, DIRECTION == Direction.Vertical
            ? 52 - (int)(ThemeManager.Current.Border.Width * 2)
            : 18 + 6 * 2 + 3 * 2) {
            Direction = DIRECTION,
            VerticalAlignment = ItemsAlignment.Center,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = style,
            Children =
            [
                new ImageNode(icon, 18, 18, ThemeManager.Current.Text),
                new TextNode(text),
            ],
        };
    }

    private BoxNode BuildStateModule(SystemStatsSnapshot stats) => new() {
        Direction = Direction.Horizontal,
        VerticalAlignment = ItemsAlignment.Center,
        HorizontalAlignment = ItemsAlignment.Center,
        Style = new Style() {
            BorderRadius = 999,
            ShadowColor = Color.Black with { A = 0.45f },
            ShadowDistance = 5.0f
        },
        Children =
        {
            Module(Icons.CPU, FormatPercent(stats.CpuPercent), _currentCpuColor, true, false),
            Module(Icons.RAM, FormatPercent(stats.RamPercent), _currentRamColor, false, false),
            Module(Icons.Temperature, FormatTemperature(stats.TemperatureCelsius), _currentTempColor, false, true),
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

        return new BoxNode() {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            Style = ModulesCommon.PopupStyle() with { Spacing = 8 },
            Children =
            [
                ModulesCommon.BuildTextWithIcon(Icons.SquareActivity, "System Info"),
                BuildGraphNode(
                    100.0f,
                    stats.CpuHistory,
                    cpuColor,
                    [
                        ModulesCommon.BuildTextWithIcon(Icons.CPU, "CPU"),
                        new TextNode(FormatPercent(stats.CpuPercent), 14)
                    ],
                    stats.GpuHistory,
                    gpuColor,
                    [
                        ModulesCommon.BuildTextWithIcon(Icons.GPU, "GPU"),
                        new TextNode(FormatPercent(stats.GpuPercent), 14)
                    ]),
                BuildGraphNode(
                    100.0f,
                    stats.RamHistory,
                    ramColor,
                    [
                        ModulesCommon.BuildTextWithIcon(Icons.RAM, "RAM"),
                        new TextNode(FormatPercent(stats.RamPercent), 14)
                    ],
                    stats.SwapHistory,
                    swapColor,
                    [
                        ModulesCommon.BuildTextWithIcon(Icons.HardDrive, "Swap"),
                        new TextNode(FormatPercent(stats.SwapPercent), 14)
                    ]),
                BuildGraphNode(
                    networkMaximum,
                    stats.DownloadHistory,
                    downloadColor,
                    [
                        ModulesCommon.BuildTextWithIcon(Icons.ArrowDown, "Download"),
                        new TextNode(FormatRate(stats.DownloadBytesPerSecond), 14)
                    ],
                    stats.UploadHistory,
                    uploadColor,
                    [
                        ModulesCommon.BuildTextWithIcon(Icons.ArrowUp, "Upload"),
                        new TextNode(FormatRate(stats.UploadBytesPerSecond), 14)
                    ]),
                ..BuildDiskSection(stats.Disks),
            ],
        };
    }

    private BoxNode BuildGraphNode(float max, IReadOnlyList<float> upData, Color upColor, ICollection<Node> upLabel,
        IReadOnlyList<float>? downData = null, Color? downColor = null, ICollection<Node>? downLabel = null)
    {
        var graphBackground = ThemeManager.Current.Panel;
        var grid = ThemeManager.Current.Text.MutedColor with {
            A = 0.22f
        };
        return new BoxNode(ModulesCommon.PopupStyle() with {
            Padding = 0
        }) {
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

        yield return ModulesCommon.BuildDivider(ThemeManager.Current.Border, GRAPH_WIDTH, 12);
        yield return new BoxNode(GRAPH_WIDTH) {
            HorizontalAlignment = ItemsAlignment.Center,
            Children = [ModulesCommon.BuildTextWithIcon(Icons.HardDrive, "Disks")],
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
        var fill = percentage switch {
            >= 90 => ThemeManager.Current.Critical,
            >= 75 => ThemeManager.Current.Warning,
            _ => Color.FromRgb(80, 180, 255),
        };

        return new BoxNode(GRAPH_WIDTH) {
            Direction = Direction.Vertical,
            Style = new Style { Spacing = 4 },
            Children =
            [
                new BoxNode(GRAPH_WIDTH)
                {
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Children =
                    [
                        new TextNode(disk.Name, maxWidth: GRAPH_WIDTH - 150, wrapping: TextWrapping.Ellipsis),
                        new TextNode($"{FormatBytes(disk.UsedBytes)} / {FormatBytes(disk.TotalBytes)}  {percentage}%"),
                    ],
                },
                new BoxNode(GRAPH_WIDTH, BAR_HEIGHT)
                {
                    Style = new Style { BackgroundColor = ThemeManager.Current.Text.MutedColor with { A = 0.35f }, BorderRadius = 5 },
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

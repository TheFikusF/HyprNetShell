using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;

namespace HyprNetShell;

internal enum PerformancePhase
{
    Frame,
    Update,
    RefreshState,
    Input,
    BeginRender,
    DrawBar,
    DrawDialog,
    SetInputRegions,
    EndRender,
    SwapBuffers,
    PaceFrame,
}

#if HYPRNETSHELL_PERFORMANCE_PROFILING
internal sealed partial class PerformanceProfiler : IDisposable
{
    private const string ENABLE_VARIABLE = "HYPRNETSHELL_PROFILE";
    private const string INTERVAL_VARIABLE = "HYPRNETSHELL_PROFILE_INTERVAL_SECONDS";
    private const int CLOCK_THREAD_CPUTIME_ID = 3;
    private const double DEFAULT_INTERVAL_SECONDS = 5.0;

    private readonly Dictionary<PerformancePhase, PhaseSamples> _samples = [];
    private readonly List<ActiveMeasurement> _activeMeasurements = [];
    private readonly Process _process;
    private readonly StreamWriter _writer;
    private readonly long _startedAt;
    private readonly long _intervalTicks;
    private long _windowStartedAt;
    private TimeSpan _windowProcessCpu;
    private LayoutFrameMetrics _layoutMetrics;
    private RendererFrameMetrics _rendererMetrics;
    private int _frames;
    private bool _disposed;

    private PerformanceProfiler(string outputPath, TimeSpan interval)
    {
        OutputPath = outputPath;
        _intervalTicks = Math.Max(1, (long)(interval.TotalSeconds * Stopwatch.Frequency));
        _startedAt = Stopwatch.GetTimestamp();
        _windowStartedAt = _startedAt;
        _process = Process.GetCurrentProcess();
        _windowProcessCpu = _process.TotalProcessorTime;
        _writer = new StreamWriter(
            new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));
        _writer.WriteLine(
            "timestamp_utc,window_seconds,frames,process_cpu_percent,phase,calls," +
            "wall_mean_us,wall_p50_us,wall_p95_us,wall_p99_us," +
            "cpu_mean_us,cpu_p50_us,cpu_p95_us,cpu_p99_us," +
            "alloc_mean_bytes,alloc_total_bytes," +
            "layouts_per_frame,box_draws_per_frame,width_measures_per_frame,height_measures_per_frame," +
            "child_arrays_per_frame,child_array_elements_per_frame," +
            "colored_requests_per_frame,colored_vertices_per_frame,text_draws_per_frame,texture_draws_per_frame," +
            "colored_flushes_per_frame,gl_draw_calls_per_frame,buffer_uploads_per_frame,buffer_upload_bytes_per_frame," +
            "rounded_rects_per_frame,rounded_borders_per_frame,shadows_per_frame");
    }

    public string OutputPath { get; }
    public bool Enabled => true;

    public static PerformanceProfiler? TryCreate()
    {
        if (!ReadBooleanSwitch(ENABLE_VARIABLE))
        {
            return null;
        }

        try
        {
            var intervalSeconds = DEFAULT_INTERVAL_SECONDS;
            var configuredInterval = Environment.GetEnvironmentVariable(INTERVAL_VARIABLE);
            if (double.TryParse(configuredInterval, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                intervalSeconds = Math.Clamp(parsed, 1.0, 300.0);
            }

            var directory = GetStateDirectory();
            Directory.CreateDirectory(directory);
            var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            return new PerformanceProfiler(
                Path.Combine(directory, $"performance-{timestamp}.csv"),
                TimeSpan.FromSeconds(intervalSeconds));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Performance", "Could not start performance profiling", exception);
            return null;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    public static void Begin(PerformanceProfiler? profiler, PerformancePhase phase) =>
        profiler?.BeginCore(phase);

    [Conditional(PerformanceProfiling.Symbol)]
    public static void End(PerformanceProfiler? profiler, PerformancePhase phase) =>
        profiler?.EndCore(phase);

    [Conditional(PerformanceProfiling.Symbol)]
    public static void AddFrameMetrics(
        PerformanceProfiler? profiler,
        LayoutFrameMetrics layout,
        RendererFrameMetrics renderer)
    {
        if (profiler is null)
        {
            return;
        }

        profiler._layoutMetrics += layout;
        profiler._rendererMetrics += renderer;
    }

    [Conditional(PerformanceProfiling.Symbol)]
    public static void CompleteFrame(PerformanceProfiler? profiler)
    {
        if (profiler is null)
        {
            return;
        }

        profiler._frames++;
        var now = Stopwatch.GetTimestamp();
        if (now - profiler._windowStartedAt >= profiler._intervalTicks)
        {
            profiler.WriteWindow(now);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (_frames > 0)
            {
                WriteWindow(Stopwatch.GetTimestamp());
            }

            _writer.Dispose();
            _process.Dispose();
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Performance", "Could not finish performance profile", exception);
        }
    }

    private void BeginCore(PerformancePhase phase)
    {
        _activeMeasurements.Add(new ActiveMeasurement(
            phase,
            Stopwatch.GetTimestamp(),
            GetThreadCpuNanoseconds(),
            GC.GetAllocatedBytesForCurrentThread()));
    }

    private void EndCore(PerformancePhase phase)
    {
        if (_activeMeasurements.Count == 0 || _activeMeasurements[^1].Phase != phase)
        {
            throw new InvalidOperationException($"Performance phase {phase} was ended out of order.");
        }

        var measurement = _activeMeasurements[^1];
        _activeMeasurements.RemoveAt(_activeMeasurements.Count - 1);
        Record(
            phase,
            Stopwatch.GetTimestamp() - measurement.WallStartedAt,
            Math.Max(0, GetThreadCpuNanoseconds() - measurement.CpuStartedAt),
            Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - measurement.AllocatedStartedAt));
    }

    private void Record(PerformancePhase phase, long wallTicks, long cpuNanoseconds, long allocatedBytes)
    {
        if (!_samples.TryGetValue(phase, out var samples))
        {
            samples = new PhaseSamples();
            _samples.Add(phase, samples);
        }

        samples.Add(wallTicks, cpuNanoseconds, allocatedBytes);
    }

    private void WriteWindow(long now)
    {
        var elapsedSeconds = (now - _windowStartedAt) / (double)Stopwatch.Frequency;
        var processCpu = _process.TotalProcessorTime;
        var processCpuPercent = elapsedSeconds > 0
            ? (processCpu - _windowProcessCpu).TotalSeconds / elapsedSeconds * 100.0
            : 0.0;
        var timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        foreach (var phase in Enum.GetValues<PerformancePhase>())
        {
            if (!_samples.TryGetValue(phase, out var samples) || samples.Count == 0)
            {
                continue;
            }

            var wall = samples.SummarizeWall();
            var cpu = samples.SummarizeCpu();
            WriteCsvRow(timestamp, elapsedSeconds, processCpuPercent, phase, samples, wall, cpu);
        }

        _writer.Flush();

        var frameCpu = GetCpuSummary(PerformancePhase.Frame);
        var drawCpu = GetCpuSummary(PerformancePhase.DrawBar);
        var paceWall = GetWallSummary(PerformancePhase.PaceFrame);
        var allocations = GetAllocationMean(PerformancePhase.Frame);
        AppLogger.Info(
            "Performance",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{elapsedSeconds:F1}s window, {_frames} frames, process CPU {processCpuPercent:F1}%, " +
                $"main-thread frame CPU p50/p95 {frameCpu.P50 / 1000.0:F3}/{frameCpu.P95 / 1000.0:F3} ms, " +
                $"bar CPU p50/p95 {drawCpu.P50 / 1000.0:F3}/{drawCpu.P95 / 1000.0:F3} ms, " +
                $"pace wall p50 {paceWall.P50 / 1000.0:F3} ms, allocations {allocations / 1024.0:F1} KiB/frame; " +
                $"CSV: {OutputPath}"));

        _samples.Clear();
        _layoutMetrics = default;
        _rendererMetrics = default;
        _frames = 0;
        _windowStartedAt = now;
        _windowProcessCpu = processCpu;
    }

    private void WriteCsvRow(
        string timestamp,
        double elapsedSeconds,
        double processCpuPercent,
        PerformancePhase phase,
        PhaseSamples samples,
        Distribution wall,
        Distribution cpu)
    {
        var frameDivisor = Math.Max(1, _frames);
        _writer.Write(string.Create(
            CultureInfo.InvariantCulture,
            $"{timestamp},{elapsedSeconds:F6},{_frames},{processCpuPercent:F3},{phase},{samples.Count}," +
            $"{wall.Mean:F3},{wall.P50:F3},{wall.P95:F3},{wall.P99:F3}," +
            $"{cpu.Mean:F3},{cpu.P50:F3},{cpu.P95:F3},{cpu.P99:F3}," +
            $"{samples.AllocatedBytes / (double)samples.Count:F3},{samples.AllocatedBytes}," +
            $"{_layoutMetrics.LayoutsDrawn / (double)frameDivisor:F3}," +
            $"{_layoutMetrics.BoxDraws / (double)frameDivisor:F3}," +
            $"{_layoutMetrics.WidthMeasurements / (double)frameDivisor:F3}," +
            $"{_layoutMetrics.HeightMeasurements / (double)frameDivisor:F3}," +
            $"{_layoutMetrics.ChildArrayAllocations / (double)frameDivisor:F3}," +
            $"{_layoutMetrics.ChildArrayElements / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.ColoredDrawRequests / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.ColoredVertices / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.TextDraws / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.TextureDraws / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.ColoredFlushes / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.GlDrawCalls / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.BufferUploads / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.BufferUploadBytes / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.RoundedRects / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.RoundedBorders / (double)frameDivisor:F3}," +
            $"{_rendererMetrics.Shadows / (double)frameDivisor:F3}\n"));
    }

    private Distribution GetCpuSummary(PerformancePhase phase) =>
        _samples.TryGetValue(phase, out var samples) ? samples.SummarizeCpu() : default;

    private Distribution GetWallSummary(PerformancePhase phase) =>
        _samples.TryGetValue(phase, out var samples) ? samples.SummarizeWall() : default;

    private double GetAllocationMean(PerformancePhase phase) =>
        _samples.TryGetValue(phase, out var samples) && samples.Count > 0
            ? samples.AllocatedBytes / (double)samples.Count
            : 0.0;

    private static string GetStateDirectory()
    {
        var stateDirectory = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            stateDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "state");
        }

        return Path.Combine(stateDirectory, "hyprnetshell");
    }

    private static bool ReadBooleanSwitch(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static long GetThreadCpuNanoseconds()
    {
        return clock_gettime(CLOCK_THREAD_CPUTIME_ID, out var time) == 0
            ? checked(time.Seconds * 1_000_000_000L + time.Nanoseconds)
            : 0;
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int clock_gettime(int clockId, out Timespec time);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Timespec
    {
        public readonly long Seconds;
        public readonly long Nanoseconds;
    }

    private readonly record struct ActiveMeasurement(
        PerformancePhase Phase,
        long WallStartedAt,
        long CpuStartedAt,
        long AllocatedStartedAt);

    private sealed class PhaseSamples
    {
        private readonly List<long> _wallTicks = [];
        private readonly List<long> _cpuNanoseconds = [];

        public int Count => _wallTicks.Count;
        public long AllocatedBytes { get; private set; }

        public void Add(long wallTicks, long cpuNanoseconds, long allocatedBytes)
        {
            _wallTicks.Add(wallTicks);
            _cpuNanoseconds.Add(cpuNanoseconds);
            AllocatedBytes += allocatedBytes;
        }

        public Distribution SummarizeWall() => Summarize(_wallTicks, 1_000_000.0 / Stopwatch.Frequency);
        public Distribution SummarizeCpu() => Summarize(_cpuNanoseconds, 0.001);

        private static Distribution Summarize(List<long> values, double scale)
        {
            if (values.Count == 0)
            {
                return default;
            }

            var sorted = values.ToArray();
            Array.Sort(sorted);
            long total = 0;
            foreach (var value in sorted)
            {
                total += value;
            }

            return new Distribution(
                total / (double)sorted.Length * scale,
                Percentile(sorted, 0.50) * scale,
                Percentile(sorted, 0.95) * scale,
                Percentile(sorted, 0.99) * scale);
        }

        private static long Percentile(long[] sorted, double percentile)
        {
            var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
            return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
        }
    }

    private readonly record struct Distribution(double Mean, double P50, double P95, double P99);
}
#else
internal sealed class PerformanceProfiler
{
    [Conditional(PerformanceProfiling.Symbol)]
    public static void Begin(PerformanceProfiler? profiler, PerformancePhase phase)
    {
    }

    [Conditional(PerformanceProfiling.Symbol)]
    public static void End(PerformanceProfiler? profiler, PerformancePhase phase)
    {
    }

    [Conditional(PerformanceProfiling.Symbol)]
    public static void AddFrameMetrics(
        PerformanceProfiler? profiler,
        LayoutFrameMetrics layout,
        RendererFrameMetrics renderer)
    {
    }

    [Conditional(PerformanceProfiling.Symbol)]
    public static void CompleteFrame(PerformanceProfiler? profiler)
    {
    }
}
#endif

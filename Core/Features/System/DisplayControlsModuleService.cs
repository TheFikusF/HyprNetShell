using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class DisplayControlsModuleService : IBarDataService
{
    private const int DEFAULT_TEMPERATURE = 6000;
    private static readonly SemaphoreSlim HyprsunsetLock = new(1, 1);
    private readonly object _curveLock = new();
    private readonly object _temperatureQueueLock = new();
    private readonly IHyprctl _hyprctl;
    private readonly string _curvePath = GetCurvePath();
    private TemperatureCurvePoint[] _temperatureCurve;
    private bool _automaticTemperatureEnabled;
    private CancellationTokenSource? _persistCurveCts;
    private int _latestQueuedTemperature;
    private int _sentQueuedTemperature = int.MinValue;
    private bool _temperatureQueueRunning;
    private bool? _hyprsunsetInstalled;
    private int _temperature = DEFAULT_TEMPERATURE;
    private DateTime _nextTemperatureUpdate = DateTime.MinValue;

    public DisplayControlsModuleService(IHyprctl hyprctl)
    {
        _hyprctl = hyprctl;
        (_temperatureCurve, _automaticTemperatureEnabled) = LoadSchedule(_curvePath);
    }

    internal IReadOnlyList<TemperatureCurvePoint> GetTemperatureCurve()
    {
        lock (_curveLock)
        {
            return [.._temperatureCurve];
        }
    }

    internal bool IsAutomaticTemperatureEnabled()
    {
        lock (_curveLock)
        {
            return _automaticTemperatureEnabled;
        }
    }

    public async ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        var temperature = await _hyprctl.GetColorTemperatureAsync(cancellationToken);
        var running = temperature.HasValue;
        if (running)
        {
            _hyprsunsetInstalled = true;
            _temperature = temperature!.Value;
        }
        else if (!_hyprsunsetInstalled.HasValue)
        {
            var version = await CommandRunner.TryReadAsync(
                "hyprsunset",
                "--version",
                TimeSpan.FromMilliseconds(500),
                cancellationToken);
            _hyprsunsetInstalled = !string.IsNullOrWhiteSpace(version);
        }

        bool automaticTemperatureEnabled;
        lock (_curveLock)
        {
            automaticTemperatureEnabled = _automaticTemperatureEnabled;
        }

        if (_hyprsunsetInstalled == true && automaticTemperatureEnabled && DateTime.Now >= _nextTemperatureUpdate)
        {
            TemperatureCurvePoint[] curve;
            lock (_curveLock)
            {
                curve = [.._temperatureCurve];
            }

            var now = DateTime.Now;
            _temperature = TemperatureCurveMath.Evaluate(curve, now.Hour + now.Minute / 60.0f);
            await SetTemperatureAsync(_temperature);
            _nextTemperatureUpdate = now.AddMinutes(5);
        }

        TemperatureCurvePoint[] snapshotCurve;
        lock (_curveLock)
        {
            snapshotCurve = [.._temperatureCurve];
        }

        state.DisplayControls = new DisplayControlsSnapshot(
            ReadBacklight("/sys/class/backlight"),
            ReadBacklight("/sys/class/leds", IsKeyboardBacklight),
            _hyprsunsetInstalled == true,
            running,
            _temperature,
            snapshotCurve,
            automaticTemperatureEnabled);
    }

    internal void SetCurvePoint(int index, float hour, int temperatureKelvin)
    {
        lock (_curveLock)
        {
            if ((uint)index >= (uint)_temperatureCurve.Length)
            {
                return;
            }

            var minimumHour = index == 0 ? 0.0f : _temperatureCurve[index - 1].Hour + 0.25f;
            var maximumHour = index == _temperatureCurve.Length - 1
                ? 24.0f
                : _temperatureCurve[index + 1].Hour - 0.25f;
            hour = Math.Clamp(MathF.Round(hour * 4.0f) / 4.0f, minimumHour, maximumHour);
            _temperatureCurve[index] = new TemperatureCurvePoint(
                hour,
                Math.Clamp(temperatureKelvin,
                    TemperatureCurveMath.MINIMUM_TEMPERATURE,
                    TemperatureCurveMath.MAXIMUM_TEMPERATURE));
        }

        ScheduleCurvePersistence();
        ApplyCurveImmediatelyIfEnabled();
    }

    internal void SetAutomaticTemperatureEnabled(bool enabled)
    {
        lock (_curveLock)
        {
            if (_automaticTemperatureEnabled == enabled)
            {
                return;
            }

            _automaticTemperatureEnabled = enabled;
        }

        ScheduleCurvePersistence();
        if (enabled)
        {
            ApplyCurveImmediatelyIfEnabled();
        }
    }

    internal static async Task SetBacklightAsync(BacklightSnapshot backlight, int percentage)
    {
        var value = (int)Math.Round(
            backlight.Maximum * Math.Clamp(percentage, 0, 100) / 100.0,
            MidpointRounding.AwayFromZero);
        var subsystem = Path.GetFileName(Path.GetDirectoryName(backlight.DevicePath));
        var result = await CommandRunner.TryReadAsync(
            "busctl",
            $"call org.freedesktop.login1 /org/freedesktop/login1/session/self " +
            $"org.freedesktop.login1.Session SetBrightness ssu {subsystem} {backlight.Name} {value}",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        if (result is not null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(backlight.DevicePath, "brightness"),
                value.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // The device may disappear or deny writes; the next snapshot restores the real value.
        }
    }

    internal async Task SetTemperatureAsync(int temperatureKelvin)
    {
        temperatureKelvin = Math.Clamp(temperatureKelvin, 2000, 6500);
        await HyprsunsetLock.WaitAsync();
        try
        {
            if (await _hyprctl.SetColorTemperatureAsync(temperatureKelvin))
            {
                return;
            }

            StartHyprsunset(temperatureKelvin);
            await Task.Delay(150);
        }
        finally
        {
            HyprsunsetLock.Release();
        }
    }

    private static BacklightSnapshot? ReadBacklight(
        string root,
        Func<string, bool>? predicate = null)
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(root).OrderBy(x => x, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path);
                if (predicate is not null && !predicate(name))
                {
                    continue;
                }

                if (TryReadInt(Path.Combine(path, "brightness"), out var value) &&
                    TryReadInt(Path.Combine(path, "max_brightness"), out var maximum) &&
                    maximum > 0)
                {
                    return new BacklightSnapshot(path, name, value, maximum);
                }
            }
        }
        catch
        {
            // Treat inaccessible sysfs classes as unavailable.
        }

        return null;
    }

    private static bool TryReadInt(string path, out int value)
    {
        try
        {
            return int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value);
        }
        catch
        {
            value = 0;
            return false;
        }
    }

    private static bool IsKeyboardBacklight(string name) =>
        name.Contains("kbd", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("keyboard", StringComparison.OrdinalIgnoreCase);

    private void ScheduleCurvePersistence()
    {
        _persistCurveCts?.Cancel();
        _persistCurveCts?.Dispose();
        _persistCurveCts = new CancellationTokenSource();
        var cancellationToken = _persistCurveCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(350, cancellationToken);
                TemperatureCurvePoint[] curve;
                bool enabled;
                lock (_curveLock)
                {
                    curve = [.._temperatureCurve];
                    enabled = _automaticTemperatureEnabled;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_curvePath)!);
                await File.WriteAllTextAsync(
                    _curvePath,
                    JsonSerializer.Serialize(
                        new TemperatureScheduleConfig(enabled, curve),
                        DisplayControlsJsonContext.Default.TemperatureScheduleConfig),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // A newer drag update will persist the final curve.
            }
            catch
            {
                // Keep the in-memory curve if the config directory is not writable.
            }
        }, cancellationToken);
    }

    private static (TemperatureCurvePoint[] Points, bool Enabled) LoadSchedule(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                var legacyPoints = JsonSerializer.Deserialize(
                    json,
                    DisplayControlsJsonContext.Default.TemperatureCurvePointArray);
                return legacyPoints is { Length: 4 }
                    ? (NormalizeCurve(legacyPoints), true)
                    : ([..TemperatureCurveMath.DefaultPoints], true);
            }

            var config = JsonSerializer.Deserialize(
                json,
                DisplayControlsJsonContext.Default.TemperatureScheduleConfig);
            return config?.Points is { Length: 4 }
                ? (NormalizeCurve(config.Points), config.Enabled)
                : ([..TemperatureCurveMath.DefaultPoints], true);
        }
        catch
        {
            // Fall back to a useful day/night curve.
        }

        return ([..TemperatureCurveMath.DefaultPoints], true);
    }

    private static TemperatureCurvePoint[] NormalizeCurve(IReadOnlyList<TemperatureCurvePoint> points)
    {
        var ordered = points
            .Select(point => new TemperatureCurvePoint(
                Math.Clamp(MathF.Round(point.Hour * 4.0f) / 4.0f, 0.0f, 24.0f),
                Math.Clamp(point.TemperatureKelvin,
                    TemperatureCurveMath.MINIMUM_TEMPERATURE,
                    TemperatureCurveMath.MAXIMUM_TEMPERATURE)))
            .OrderBy(point => point.Hour)
            .ToArray();

        for (var i = 0; i < ordered.Length; i++)
        {
            var minimumHour = i == 0 ? 0.0f : ordered[i - 1].Hour + 0.25f;
            var maximumHour = 24.0f - (ordered.Length - 1 - i) * 0.25f;
            ordered[i] = ordered[i] with { Hour = Math.Clamp(ordered[i].Hour, minimumHour, maximumHour) };
        }

        return ordered;
    }

    private void ApplyCurveImmediatelyIfEnabled()
    {
        TemperatureCurvePoint[] curve;
        lock (_curveLock)
        {
            if (!_automaticTemperatureEnabled)
            {
                return;
            }

            curve = [.._temperatureCurve];
        }

        var now = DateTime.Now;
        var temperature = TemperatureCurveMath.Evaluate(curve,
            now.Hour + now.Minute / 60.0f + now.Second / 3600.0f);
        _nextTemperatureUpdate = now.AddMinutes(5);
        QueueTemperatureUpdate(temperature);
    }

    private void QueueTemperatureUpdate(int temperature)
    {
        lock (_temperatureQueueLock)
        {
            _latestQueuedTemperature = temperature;
            if (_temperatureQueueRunning)
            {
                return;
            }

            _temperatureQueueRunning = true;
        }

        _ = Task.Run(ProcessTemperatureQueueAsync);
    }

    private async Task ProcessTemperatureQueueAsync()
    {
        while (true)
        {
            int temperature;
            lock (_temperatureQueueLock)
            {
                if (_sentQueuedTemperature == _latestQueuedTemperature)
                {
                    _temperatureQueueRunning = false;
                    return;
                }

                temperature = _latestQueuedTemperature;
                _sentQueuedTemperature = temperature;
            }

            if (!IsAutomaticTemperatureEnabled())
            {
                lock (_temperatureQueueLock)
                {
                    _sentQueuedTemperature = int.MinValue;
                    _temperatureQueueRunning = false;
                }
                return;
            }

            _temperature = temperature;
            await SetTemperatureAsync(temperature);
            await Task.Delay(50);
        }
    }

    private static string GetCurvePath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configRoot, "hyprnetshell", "temperature-curve.json");
    }

    private static void StartHyprsunset(int temperatureKelvin)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "hyprsunset",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--temperature");
            startInfo.ArgumentList.Add(temperatureKelvin.ToString(CultureInfo.InvariantCulture));
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            // The popup keeps the control marked unavailable if startup fails.
        }
    }
}

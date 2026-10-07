using System.Diagnostics;
using System.Globalization;
using HyprNetShell.Core.Configuration;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class DisplayControlsModuleService : IBarDataService
{
    private const int DEFAULT_TEMPERATURE = 6000;
    private static readonly TimeSpan BacklightRefreshInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TemperatureRecoveryInterval = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim HyprsunsetLock = new(1, 1);
    private readonly object _curveLock = new();
    private readonly object _temperatureQueueLock = new();
    private readonly object _brightnessQueueLock = new();
    private readonly IHyprctl _hyprctl;
    private readonly AppConfigurationStore _configuration = AppConfigurationStore.Shared;
    private TemperatureCurvePoint[] _temperatureCurve;
    private BrightnessCurvePoint[] _brightnessCurve;
    private bool _automaticTemperatureEnabled;
    private bool _automaticBrightnessEnabled;
    private int _latestQueuedTemperature;
    private int _sentQueuedTemperature = int.MinValue;
    private bool _temperatureQueueRunning;
    private int _latestQueuedBrightness;
    private int _sentQueuedBrightness = int.MinValue;
    private bool _brightnessQueueRunning;
    private bool? _hyprsunsetInstalled;
    private int _temperature = DEFAULT_TEMPERATURE;
    private DateTime _nextTemperatureUpdate = DateTime.MinValue;
    private DateTime _nextBrightnessUpdate = DateTime.MinValue;
    private DateTime _nextBacklightRefreshUtc = DateTime.MinValue;
    private DateTime _nextTemperatureRecoveryUtc = DateTime.MinValue;
    private DisplayControlsSnapshot _snapshot = DisplayControlsSnapshot.Empty;

    public DisplayControlsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public DisplayControlsModuleService(IHyprctl hyprctl)
    {
        _hyprctl = hyprctl;
        var display = _configuration.Snapshot.Display;
        _temperatureCurve = NormalizeTemperatureCurve(display.Temperature.Points
            .Select(point => new TemperatureCurvePoint(point.Hour, point.Value)));
        _automaticTemperatureEnabled = display.Temperature.Enabled;
        _brightnessCurve = NormalizeBrightnessCurve(display.Brightness.Points
            .Select(point => new BrightnessCurvePoint(point.Hour, point.Value)));
        _automaticBrightnessEnabled = display.Brightness.Enabled;
    }

    internal IReadOnlyList<TemperatureCurvePoint> GetTemperatureCurve()
    {
        lock (_curveLock)
        {
            return [.. _temperatureCurve];
        }
    }

    internal bool IsAutomaticTemperatureEnabled()
    {
        lock (_curveLock)
        {
            return _automaticTemperatureEnabled;
        }
    }

    internal IReadOnlyList<BrightnessCurvePoint> GetBrightnessCurve()
    {
        lock (_curveLock)
        {
            return [.. _brightnessCurve];
        }
    }

    internal bool IsAutomaticBrightnessEnabled()
    {
        lock (_curveLock)
        {
            return _automaticBrightnessEnabled;
        }
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var utcNow = DateTime.UtcNow;
        var refreshBacklights = utcNow >= _nextBacklightRefreshUtc;
        var recoverTemperature = utcNow >= _nextTemperatureRecoveryUtc;

        bool automaticTemperatureEnabled;
        bool automaticBrightnessEnabled;
        lock (_curveLock)
        {
            automaticTemperatureEnabled = _automaticTemperatureEnabled;
            automaticBrightnessEnabled = _automaticBrightnessEnabled;
        }

        var localNow = DateTime.Now;
        var updateAutomaticTemperature = automaticTemperatureEnabled && localNow >= _nextTemperatureUpdate;
        var updateAutomaticBrightness = automaticBrightnessEnabled && localNow >= _nextBrightnessUpdate;
        if (!refreshBacklights && !recoverTemperature && !updateAutomaticTemperature && !updateAutomaticBrightness)
        {
            return;
        }

        var current = Snapshot;
        var running = current.HyprsunsetRunning;
        if (recoverTemperature)
        {
            _nextTemperatureRecoveryUtc = utcNow + TemperatureRecoveryInterval;
            var temperature = await _hyprctl.GetColorTemperatureAsync(cancellationToken);
            running = temperature.HasValue;
            if (temperature.HasValue)
            {
                _hyprsunsetInstalled = true;
                _temperature = temperature.Value;
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
        }

        if (_hyprsunsetInstalled == true && updateAutomaticTemperature)
        {
            TemperatureCurvePoint[] curve;
            lock (_curveLock)
            {
                curve = [.. _temperatureCurve];
            }

            _temperature = TemperatureCurveMath.Evaluate(curve, CurrentHour(localNow));
            await SetTemperatureAsync(_temperature);
            running = Snapshot.HyprsunsetRunning;
            _nextTemperatureUpdate = localNow.AddMinutes(5);
        }

        BacklightSnapshot? display = current.Display;
        BacklightSnapshot? keyboard = current.Keyboard;
        if (refreshBacklights)
        {
            _nextBacklightRefreshUtc = utcNow + BacklightRefreshInterval;
            display = ReadBacklight("/sys/class/backlight");
            keyboard = ReadBacklight("/sys/class/leds", IsKeyboardBacklight);
        }

        if (updateAutomaticBrightness && display is not null)
        {
            BrightnessCurvePoint[] curve;
            lock (_curveLock)
            {
                curve = [.. _brightnessCurve];
            }

            var percentage = BrightnessCurveMath.Evaluate(curve, CurrentHour(localNow));
            await SetBacklightAsync(display, percentage);
            display = display with {
                Value = (int)Math.Round(display.Maximum * percentage / 100.0, MidpointRounding.AwayFromZero),
            };
            _nextBrightnessUpdate = localNow.AddMinutes(5);
        }

        TemperatureCurvePoint[] snapshotTemperatureCurve;
        BrightnessCurvePoint[] snapshotBrightnessCurve;
        lock (_curveLock)
        {
            snapshotTemperatureCurve = [.. _temperatureCurve];
            snapshotBrightnessCurve = [.. _brightnessCurve];
        }

        Volatile.Write(ref _snapshot, new DisplayControlsSnapshot(
            display,
            keyboard,
            _hyprsunsetInstalled == true,
            running,
            _temperature,
            snapshotTemperatureCurve,
            automaticTemperatureEnabled,
            snapshotBrightnessCurve,
            automaticBrightnessEnabled));
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

        PersistCurves();
        ApplyTemperatureCurveImmediatelyIfEnabled();
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

        PersistCurves();
        if (enabled)
        {
            ApplyTemperatureCurveImmediatelyIfEnabled();
        }
    }

    internal void SetBrightnessCurvePoint(int index, float hour, int percentage)
    {
        lock (_curveLock)
        {
            if ((uint)index >= (uint)_brightnessCurve.Length)
            {
                return;
            }

            var minimumHour = index == 0 ? 0.0f : _brightnessCurve[index - 1].Hour + 0.25f;
            var maximumHour = index == _brightnessCurve.Length - 1
                ? 24.0f
                : _brightnessCurve[index + 1].Hour - 0.25f;
            _brightnessCurve[index] = new BrightnessCurvePoint(
                Math.Clamp(MathF.Round(hour * 4.0f) / 4.0f, minimumHour, maximumHour),
                Math.Clamp(percentage, BrightnessCurveMath.MINIMUM_BRIGHTNESS, BrightnessCurveMath.MAXIMUM_BRIGHTNESS));
        }

        PersistCurves();
        ApplyBrightnessCurveImmediatelyIfEnabled();
    }

    internal void SetAutomaticBrightnessEnabled(bool enabled)
    {
        lock (_curveLock)
        {
            if (_automaticBrightnessEnabled == enabled)
            {
                return;
            }

            _automaticBrightnessEnabled = enabled;
        }

        PersistCurves();
        if (enabled)
        {
            ApplyBrightnessCurveImmediatelyIfEnabled();
        }
    }

    internal async Task SetBacklightAsync(BacklightSnapshot backlight, int percentage)
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
            PublishBacklightValue(backlight, value);
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(backlight.DevicePath, "brightness"),
                value.ToString(CultureInfo.InvariantCulture));
            PublishBacklightValue(backlight, value);
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
                PublishTemperature(temperatureKelvin, running: true);
                return;
            }

            if (StartHyprsunset(temperatureKelvin))
            {
                PublishTemperature(temperatureKelvin, running: true);
                await Task.Delay(150);
            }
        }
        finally
        {
            HyprsunsetLock.Release();
        }
    }

    private void PublishBacklightValue(BacklightSnapshot backlight, int value)
    {
        var current = Snapshot;
        var updated = backlight with {
            Value = value
        };
        Volatile.Write(ref _snapshot, string.Equals(current.Display?.DevicePath, backlight.DevicePath, StringComparison.Ordinal)
            ? current with {
                Display = updated
            }
            : current with {
                Keyboard = updated
            });
    }

    private void PublishTemperature(int temperatureKelvin, bool running)
    {
        _temperature = temperatureKelvin;
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with {
            HyprsunsetInstalled = true,
            HyprsunsetRunning = running,
            TemperatureKelvin = temperatureKelvin,
        });
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

    private void PersistCurves()
    {
        TemperatureCurvePoint[] temperatureCurve;
        BrightnessCurvePoint[] brightnessCurve;
        bool temperatureEnabled;
        bool brightnessEnabled;
        lock (_curveLock)
        {
            temperatureCurve = [.. _temperatureCurve];
            brightnessCurve = [.. _brightnessCurve];
            temperatureEnabled = _automaticTemperatureEnabled;
            brightnessEnabled = _automaticBrightnessEnabled;
        }

        _configuration.Update(config =>
        {
            config.Display.Temperature.Enabled = temperatureEnabled;
            config.Display.Temperature.Points = temperatureCurve
                .Select(point => new CurvePointConfiguration { Hour = point.Hour, Value = point.TemperatureKelvin })
                .ToList();
            config.Display.Brightness.Enabled = brightnessEnabled;
            config.Display.Brightness.Points = brightnessCurve
                .Select(point => new CurvePointConfiguration { Hour = point.Hour, Value = point.Percentage })
                .ToList();
        });
    }

    private static TemperatureCurvePoint[] NormalizeTemperatureCurve(IEnumerable<TemperatureCurvePoint> points)
    {
        var normalized = NormalizeCurve(
            points.Select(point => (point.Hour, point.TemperatureKelvin)),
            TemperatureCurveMath.MINIMUM_TEMPERATURE,
            TemperatureCurveMath.MAXIMUM_TEMPERATURE,
            TemperatureCurveMath.DefaultPoints.Select(point => (point.Hour, point.TemperatureKelvin)));
        return normalized.Select(point => new TemperatureCurvePoint(point.Hour, point.Value)).ToArray();
    }

    private static BrightnessCurvePoint[] NormalizeBrightnessCurve(IEnumerable<BrightnessCurvePoint> points)
    {
        var normalized = NormalizeCurve(
            points.Select(point => (point.Hour, point.Percentage)),
            BrightnessCurveMath.MINIMUM_BRIGHTNESS,
            BrightnessCurveMath.MAXIMUM_BRIGHTNESS,
            BrightnessCurveMath.DefaultPoints.Select(point => (point.Hour, point.Percentage)));
        return normalized.Select(point => new BrightnessCurvePoint(point.Hour, point.Value)).ToArray();
    }

    private static (float Hour, int Value)[] NormalizeCurve(
        IEnumerable<(float Hour, int Value)> points,
        int minimumValue,
        int maximumValue,
        IEnumerable<(float Hour, int Value)> defaults)
    {
        var ordered = points
            .Select(point => (
                Math.Clamp(MathF.Round(point.Hour * 4.0f) / 4.0f, 0.0f, 24.0f),
                Math.Clamp(point.Value, minimumValue, maximumValue)))
            .OrderBy(point => point.Item1)
            .ToArray();
        if (ordered.Length != 4)
        {
            ordered = defaults.ToArray();
        }

        for (var i = 0; i < ordered.Length; i++)
        {
            var minimumHour = i == 0 ? 0.0f : ordered[i - 1].Item1 + 0.25f;
            var maximumHour = 24.0f - (ordered.Length - 1 - i) * 0.25f;
            ordered[i] = (Math.Clamp(ordered[i].Item1, minimumHour, maximumHour), ordered[i].Item2);
        }

        return ordered;
    }

    private void ApplyTemperatureCurveImmediatelyIfEnabled()
    {
        TemperatureCurvePoint[] curve;
        lock (_curveLock)
        {
            if (!_automaticTemperatureEnabled)
            {
                return;
            }

            curve = [.. _temperatureCurve];
        }

        var now = DateTime.Now;
        var temperature = TemperatureCurveMath.Evaluate(curve, CurrentHour(now));
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

    private void ApplyBrightnessCurveImmediatelyIfEnabled()
    {
        BrightnessCurvePoint[] curve;
        BacklightSnapshot? display;
        lock (_curveLock)
        {
            if (!_automaticBrightnessEnabled)
            {
                return;
            }

            curve = [.. _brightnessCurve];
            display = Snapshot.Display;
        }

        if (display is null)
        {
            return;
        }

        var now = DateTime.Now;
        _nextBrightnessUpdate = now.AddMinutes(5);
        QueueBrightnessUpdate(BrightnessCurveMath.Evaluate(curve, CurrentHour(now)));
    }

    private void QueueBrightnessUpdate(int percentage)
    {
        lock (_brightnessQueueLock)
        {
            _latestQueuedBrightness = percentage;
            if (_brightnessQueueRunning)
            {
                return;
            }

            _brightnessQueueRunning = true;
        }

        _ = Task.Run(ProcessBrightnessQueueAsync);
    }

    private async Task ProcessBrightnessQueueAsync()
    {
        while (true)
        {
            int percentage;
            lock (_brightnessQueueLock)
            {
                if (_sentQueuedBrightness == _latestQueuedBrightness)
                {
                    _brightnessQueueRunning = false;
                    return;
                }

                percentage = _latestQueuedBrightness;
                _sentQueuedBrightness = percentage;
            }

            if (!IsAutomaticBrightnessEnabled() || Snapshot.Display is not { } display)
            {
                lock (_brightnessQueueLock)
                {
                    _sentQueuedBrightness = int.MinValue;
                    _brightnessQueueRunning = false;
                }
                return;
            }

            await SetBacklightAsync(display, percentage);
            await Task.Delay(50);
        }
    }

    private static float CurrentHour(DateTime now) =>
        now.Hour + now.Minute / 60.0f + now.Second / 3600.0f;

    private static bool StartHyprsunset(int temperatureKelvin)
    {
        try
        {
            var startInfo = new ProcessStartInfo {
                FileName = "hyprsunset",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--temperature");
            startInfo.ArgumentList.Add(temperatureKelvin.ToString(CultureInfo.InvariantCulture));
            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch
        {
            return false;
        }
    }
}

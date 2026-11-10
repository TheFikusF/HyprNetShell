using System.Diagnostics;
using System.Globalization;
using HyprNetShell.Core.Configuration;
using HyprNetShell.Core.Features.Sni;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using Tmds.DBus.Protocol;

namespace HyprNetShell.Core.Features.System;

internal sealed class DisplayControlsModuleService : IBarDataService, IDisposable
{
    private const string UPOWER_BUS = "org.freedesktop.UPower";
    private const string KEYBOARD_PATH = "/org/freedesktop/UPower/KbdBacklight";
    private const string KEYBOARD_INTERFACE = "org.freedesktop.UPower.KbdBacklight";

    private readonly Lock _snapshotLock = new();
    private readonly Lock _curveLock = new();
    private readonly Lock _temperatureQueueLock = new();
    private readonly Lock _brightnessQueueLock = new();

    private readonly CancellationTokenSource _monitorLifetime = new();
    private readonly Task _monitorTask;

    private readonly ColorTemperatureBackend _temperatureBackend = new();
    private readonly AppConfigurationStore _configuration = AppConfigurationStore.Shared;

    private readonly TemperatureCurvePoint[] _temperatureCurve;
    private readonly BrightnessCurvePoint[] _brightnessCurve;

    private bool _automaticTemperatureEnabled;
    private bool _automaticBrightnessEnabled;
    private int _latestQueuedTemperature;
    private int _sentQueuedTemperature = int.MinValue;
    private bool _temperatureQueueRunning;
    private int _latestQueuedBrightness;
    private int _sentQueuedBrightness = int.MinValue;
    private bool _brightnessQueueRunning;
    private int _disposed;

    private DateTime _nextTemperatureUpdate = DateTime.MinValue;
    private DateTime _nextBrightnessUpdate = DateTime.MinValue;
    private DateTime _nextBacklightRefreshUtc = DateTime.MinValue;
    private DisplayControlsSnapshot _snapshot = DisplayControlsSnapshot.Empty;

    public event Action<int>? BrightnessChanged;
    public event Action<int>? KeyboardBrightnessChanged;

    public DisplayControlsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public DisplayControlsModuleService()
    {
        var display = _configuration.Snapshot.Display;
        _temperatureCurve = NormalizeTemperatureCurve(display.Temperature.Points
            .Select(point => new TemperatureCurvePoint(point.Hour, point.Value)));
        _automaticTemperatureEnabled = display.Temperature.Enabled;
        _brightnessCurve = NormalizeBrightnessCurve(display.Brightness.Points
            .Select(point => new BrightnessCurvePoint(point.Hour, point.Value)));
        _automaticBrightnessEnabled = display.Brightness.Enabled;

        _monitorTask = Task.WhenAll(
            Task.Run(MonitorBacklightsAsync),
            Task.Run(MonitorKeyboardBrightnessAsync));
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
        var temperatureBecameAvailable = _temperatureBackend.Available && !Snapshot.TemperatureControlAvailable;

        bool automaticTemperatureEnabled;
        bool automaticBrightnessEnabled;
        lock (_curveLock)
        {
            automaticTemperatureEnabled = _automaticTemperatureEnabled;
            automaticBrightnessEnabled = _automaticBrightnessEnabled;
        }

        var localNow = DateTime.Now;
        var updateAutomaticTemperature = automaticTemperatureEnabled &&
            (localNow >= _nextTemperatureUpdate || temperatureBecameAvailable);
        var updateAutomaticBrightness = automaticBrightnessEnabled && localNow >= _nextBrightnessUpdate;
        cancellationToken.ThrowIfCancellationRequested();
        var current = Snapshot;
        if (_temperatureBackend.Available && updateAutomaticTemperature)
        {
            TemperatureCurvePoint[] curve;
            lock (_curveLock)
            {
                curve = [.. _temperatureCurve];
            }

            await SetTemperatureAsync(TemperatureCurveMath.Evaluate(curve, CurrentHour(localNow)), cancellationToken);
            _nextTemperatureUpdate = localNow.AddMinutes(5);
        }

        BacklightSnapshot? display = current.Display;
        if (refreshBacklights)
        {
            _nextBacklightRefreshUtc = DateTime.MaxValue;
            RefreshBacklights();
            display = Snapshot.Display;
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
            _nextBrightnessUpdate = localNow.AddMinutes(5);
        }

        TemperatureCurvePoint[] snapshotTemperatureCurve;
        BrightnessCurvePoint[] snapshotBrightnessCurve;
        lock (_curveLock)
        {
            snapshotTemperatureCurve = [.. _temperatureCurve];
            snapshotBrightnessCurve = [.. _brightnessCurve];
        }

        lock (_snapshotLock)
        {
            current = Snapshot;
            Volatile.Write(ref _snapshot, new DisplayControlsSnapshot(
                current.Display,
                current.Keyboard,
                _temperatureBackend.Available,
                _temperatureBackend.Active,
                _temperatureBackend.Temperature,
                snapshotTemperatureCurve,
                automaticTemperatureEnabled,
                snapshotBrightnessCurve,
                automaticBrightnessEnabled));
        }
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
            PublishBacklightValue(backlight);
            return;
        }

        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(backlight.DevicePath, "brightness"),
                value.ToString(CultureInfo.InvariantCulture));
            PublishBacklightValue(backlight);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Display", $"Could not write brightness for {backlight.Name}", exception);
        }
    }

    internal Task SetTemperatureAsync(int temperatureKelvin) =>
        SetTemperatureAsync(temperatureKelvin, CancellationToken.None);

    private async Task SetTemperatureAsync(int temperatureKelvin, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        temperatureKelvin = Math.Clamp(temperatureKelvin,
            TemperatureCurveMath.MINIMUM_TEMPERATURE,
            TemperatureCurveMath.MAXIMUM_TEMPERATURE);
        if (!await _temperatureBackend.SetAsync(temperatureKelvin, cancellationToken))
        {
            AppLogger.Warning("Display", $"Could not submit screen temperature {temperatureKelvin}K");
        }

        PublishTemperature();
    }

    private void PublishBacklightValue(BacklightSnapshot backlight)
    {
        lock (_snapshotLock)
        {
            var current = Snapshot;
            // Read back the applied value: a successful command can still be clamped by the driver.
            if (!TryReadInt(Path.Combine(backlight.DevicePath, "brightness"), out var actualValue))
            {
                return;
            }

            var updated = backlight with { Value = actualValue };
            if (string.Equals(current.Display?.DevicePath, backlight.DevicePath, StringComparison.Ordinal))
            {
                PublishBacklights(updated, current.Keyboard);
            }

            else if (string.Equals(current.Keyboard?.DevicePath, backlight.DevicePath, StringComparison.Ordinal))
            {
                PublishBacklights(current.Display, updated);
            }
        }
    }

    private void PublishTemperature()
    {
        lock (_snapshotLock)
        {
            Volatile.Write(ref _snapshot, Snapshot with {
                TemperatureControlAvailable = _temperatureBackend.Available,
                TemperatureControlActive = _temperatureBackend.Active,
                TemperatureKelvin = _temperatureBackend.Temperature,
            });
        }
    }

    private void RefreshBacklights()
    {
        lock (_snapshotLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            PublishBacklights(ReadBacklight("/sys/class/backlight"),
                ReadBacklight("/sys/class/leds", IsKeyboardBacklight));
        }
    }

    // Callers hold the snapshot lock so reads, publications and notifications stay ordered.
    private void PublishBacklights(BacklightSnapshot? display, BacklightSnapshot? keyboard)
    {
        var current = Snapshot;
        Volatile.Write(ref _snapshot, current with { Display = display, Keyboard = keyboard });
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        NotifyBacklightChanged(current.Display, display, BrightnessChanged, "Screen");
        NotifyBacklightChanged(current.Keyboard, keyboard, KeyboardBrightnessChanged, "Keyboard");
    }

    private static void NotifyBacklightChanged(BacklightSnapshot? previous, BacklightSnapshot? updated,
        Action<int>? handlers, string device)
    {
        if (previous is null || updated is null || previous.DevicePath != updated.DevicePath ||
            previous.Percentage == updated.Percentage)
        {
            return;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<int>>())
        {
            try
            {
                handler(updated.Percentage);
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Display", $"{device} brightness change subscriber failed", exception);
            }
        }
    }

    private async Task MonitorKeyboardBrightnessAsync()
    {
        var cancellationToken = _monitorLifetime.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var connection = new DBusConnection(
                    DBusAddress.System ?? throw new InvalidOperationException("The system D-Bus address is unavailable"));
                using var stop = cancellationToken.Register(connection.Dispose);
                var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                using var brightness = await AddKeyboardBrightnessSubscriptionAsync(
                    connection, "BrightnessChanged", disconnected)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                using var brightnessWithSource = await AddKeyboardBrightnessSubscriptionAsync(
                    connection, "BrightnessChangedWithSource", disconnected)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                RefreshBacklights();
                await disconnected.Task.WaitAsync(cancellationToken);
                AppLogger.Warning("Display", "Keyboard brightness D-Bus monitor disconnected; retrying in 30 seconds");
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Display", "Keyboard brightness D-Bus monitor failed; retrying in 30 seconds", exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private ValueTask<IDisposable> AddKeyboardBrightnessSubscriptionAsync(
        DBusConnection connection, string member, TaskCompletionSource disconnected) =>
        connection.AddMatchAsync(
            new MatchRule {
                Type = MessageType.Signal,
                Sender = UPOWER_BUS,
                Path = KEYBOARD_PATH,
                Interface = KEYBOARD_INTERFACE,
                Member = member,
            },
            static (message, state) => message.GetBodyReader().ReadInt32(),
            static notification =>
            {
                var (service, disconnected) =
                    ((DisplayControlsModuleService, TaskCompletionSource))notification.State!;
                if (!notification.HasValue)
                {
                    disconnected.TrySetResult();
                    return;
                }

                // Read sysfs rather than assuming UPower selected the same keyboard LED.
                // Both signals can describe one write; publication suppresses duplicate values.
                service.RefreshBacklights();
            },
            false,
            Dbus.CONNECTION_FAILURE_OBSERVER_FLAGS,
            (this, disconnected));

    private async Task MonitorBacklightsAsync()
    {
        var cancellationToken = _monitorLifetime.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Periodic restart bounds each child lifetime and recovers a stuck monitor.
                using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                session.CancelAfter(TimeSpan.FromMinutes(10));
                using var process = new Process {
                    StartInfo = new ProcessStartInfo {
                        FileName = "udevadm",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                    },
                };
                foreach (var argument in new[] { "monitor", "--kernel", "--subsystem-match=backlight", "--subsystem-match=leds" })
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                process.Start();
                using var stop = session.Token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }

                    catch (Exception exception)
                    {
                        AppLogger.Warning("Display", "Could not stop backlight monitor", exception);
                    }
                });
                var errors = DrainMonitorErrorsAsync(process, session.Token);
                try
                {
                    while (await process.StandardOutput.ReadLineAsync(session.Token) is { } line)
                    {
                        if (line.StartsWith("KERNEL[", StringComparison.Ordinal))
                        {
                            RefreshBacklights();
                        }
                    }

                    await process.WaitForExitAsync(session.Token);
                    AppLogger.Warning("Display", $"Backlight monitor exited with code {process.ExitCode}; retrying in 30 seconds");
                }
                finally
                {
                    session.Cancel();
                    await errors;
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await process.WaitForExitAsync(shutdown.Token);
                }
            }
            catch (OperationCanceledException)
            {
                continue;
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Display", "Backlight event monitor failed; retrying in 30 seconds", exception);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            }
            catch (OperationCanceledException) { }
        }
    }

    private static async Task DrainMonitorErrorsAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync(cancellationToken) is { } line)
            {
                AppLogger.Warning("Display", $"Backlight monitor: {line}");
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _temperatureBackend.Dispose();
        _monitorLifetime.Cancel();
        try
        {
            if (!_monitorTask.Wait(TimeSpan.FromSeconds(2)))
            {
                AppLogger.Warning("Display", "Backlight monitor did not stop within two seconds");
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Display", "Backlight monitor shutdown failed", exception);
        }

        // If shutdown times out, release subscribers and the token source only after the worker stops.
        _ = _monitorTask.ContinueWith(
            _ =>
            {
                BrightnessChanged = null;
                KeyboardBrightnessChanged = null;
                _monitorLifetime.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        catch (DirectoryNotFoundException) { }
        catch (Exception exception)
        {
            AppLogger.Warning("Display", $"Could not discover backlights under {root}", exception);
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
        catch (Exception exception)
        {
            AppLogger.Warning("Display", $"Could not read backlight value from {path}", exception);
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

}

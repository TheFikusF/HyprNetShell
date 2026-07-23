using System.Diagnostics;
using System.Text.Json;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Features.System;

internal sealed class WallpaperModuleService : IDisposable
{
    internal const int DEFAULT_DURATION_MINUTES = 10;
    internal const int MINIMUM_DURATION_MINUTES = 1;
    internal const int MAXIMUM_DURATION_MINUTES = 120;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".jpeg", ".jpg", ".jxl", ".png", ".tif", ".tiff", ".webp",
    };

    private readonly Lock _stateLock = new();
    private readonly IHyprctl _hyprctl;
    private readonly string _configPath = GetConfigPath();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _settingsChanged = new(0, 1);
    private readonly Task _slideshowTask;
    private Process? _hyprpaperProcess;
    private bool _slideshowEnabled;
    private int _durationMinutes;
    private string? _currentWallpaper;
    private bool _disposed;

    internal WallpaperModuleService(IHyprctl hyprctl)
    {
        _hyprctl = hyprctl;
        var config = LoadConfig(_configPath);
        _slideshowEnabled = config.SlideshowEnabled;
        _durationMinutes = NormalizeDuration(config.DurationMinutes);

        StartHyprpaper();
        _slideshowTask = Task.Run(() => RunSlideshowAsync(_lifetime.Token));
    }

    internal string WallpaperDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Pictures",
        "wp");

    internal bool SlideshowEnabled
    {
        get
        {
            lock (_stateLock)
            {
                return _slideshowEnabled;
            }
        }
    }

    internal int DurationMinutes
    {
        get
        {
            lock (_stateLock)
            {
                return _durationMinutes;
            }
        }
    }

    internal IReadOnlyList<string> GetWallpaperPaths(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(WallpaperDirectory))
        {
            return [];
        }

        try
        {
            var paths = new List<string>();
            foreach (var path in Directory.EnumerateFiles(WallpaperDirectory, "*", new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         RecurseSubdirectories = true,
                     }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }

            return paths.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Wallpapers", $"Could not enumerate {WallpaperDirectory}", exception);
            return [];
        }
    }

    internal async Task SetWallpaperAsync(string path, CancellationToken cancellationToken = default)
    {
        if (await _hyprctl.SetWallpaperAsync(path, cancellationToken))
        {
            lock (_stateLock)
            {
                _currentWallpaper = path;
            }
        }
    }

    internal void SetSlideshowEnabled(bool enabled)
    {
        lock (_stateLock)
        {
            if (_slideshowEnabled == enabled)
            {
                return;
            }

            _slideshowEnabled = enabled;
        }

        SettingsWereChanged();
    }

    internal void SetDurationMinutes(int durationMinutes)
    {
        durationMinutes = NormalizeDuration(durationMinutes);
        lock (_stateLock)
        {
            if (_durationMinutes == durationMinutes)
            {
                return;
            }

            _durationMinutes = durationMinutes;
        }

        SettingsWereChanged();
    }

    private async Task RunSlideshowAsync(CancellationToken cancellationToken)
    {
        var wasEnabled = false;
        try
        {
            // Give the freshly launched daemon a moment to create its control socket.
            await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                bool enabled;
                int durationMinutes;
                lock (_stateLock)
                {
                    enabled = _slideshowEnabled;
                    durationMinutes = _durationMinutes;
                }

                if (!enabled)
                {
                    wasEnabled = false;
                    await _settingsChanged.WaitAsync(cancellationToken);
                    continue;
                }

                if (!wasEnabled)
                {
                    await AdvanceWallpaperAsync(cancellationToken);
                    wasEnabled = true;
                }

                var settingsChanged = await _settingsChanged.WaitAsync(
                    TimeSpan.FromMinutes(durationMinutes),
                    cancellationToken);
                if (!settingsChanged)
                {
                    await AdvanceWallpaperAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception exception)
        {
            AppLogger.Error("Wallpapers", "Wallpaper slideshow stopped unexpectedly", exception);
        }
    }

    private async Task AdvanceWallpaperAsync(CancellationToken cancellationToken)
    {
        var paths = GetWallpaperPaths(cancellationToken);
        if (paths.Count == 0)
        {
            return;
        }

        string? current;
        lock (_stateLock)
        {
            current = _currentWallpaper;
        }

        var currentIndex = current is null
            ? -1
            : paths.ToList().FindIndex(path => string.Equals(path, current, StringComparison.Ordinal));
        await SetWallpaperAsync(paths[(currentIndex + 1) % paths.Count], cancellationToken);
    }

    private void SettingsWereChanged()
    {
        try
        {
            if (_settingsChanged.CurrentCount == 0)
            {
                _settingsChanged.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        PersistConfig();
    }

    private void PersistConfig()
    {
        WallpaperSlideshowConfig config;
        lock (_stateLock)
        {
            config = new WallpaperSlideshowConfig(_slideshowEnabled, _durationMinutes);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(config, WallpaperJsonContext.Default.WallpaperSlideshowConfig));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Wallpapers", "Could not save wallpaper slideshow settings", exception);
        }
    }

    private static WallpaperSlideshowConfig LoadConfig(string path)
    {
        try
        {
            return JsonSerializer.Deserialize(
                       File.ReadAllText(path),
                       WallpaperJsonContext.Default.WallpaperSlideshowConfig)
                   ?? new WallpaperSlideshowConfig(true, DEFAULT_DURATION_MINUTES);
        }
        catch
        {
            return new WallpaperSlideshowConfig(true, DEFAULT_DURATION_MINUTES);
        }
    }

    private static int NormalizeDuration(int durationMinutes) =>
        Math.Clamp(durationMinutes, MINIMUM_DURATION_MINUTES, MAXIMUM_DURATION_MINUTES);

    private static string GetConfigPath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        return Path.Combine(configRoot, "hyprnetshell", "wallpapers.json");
    }

    private void StartHyprpaper()
    {
        try
        {
            _hyprpaperProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "hyprpaper",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (_hyprpaperProcess is null)
            {
                AppLogger.Warning("Wallpapers", "Could not start hyprpaper");
            }
            else
            {
                AppLogger.Info("Wallpapers", "Started hyprpaper");
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Wallpapers", "Could not start hyprpaper", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        try
        {
            _slideshowTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // Cancellation or a timeout is harmless during shutdown.
        }

        try
        {
            if (_hyprpaperProcess is { HasExited: false })
            {
                _hyprpaperProcess.Kill(true);
            }
        }
        catch
        {
            // The daemon may already have exited or detached.
        }

        _hyprpaperProcess?.Dispose();
        _settingsChanged.Dispose();
        _lifetime.Dispose();
    }
}

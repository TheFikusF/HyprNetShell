using HyprNetShell.Core.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HyprNetShell.Core.Configuration;

internal sealed class AppConfigurationStore
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly Timer _saveTimer;
    private AppConfiguration _configuration;

    internal static AppConfigurationStore Shared { get; } = new();

    internal AppConfiguration Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _configuration;
            }
        }
    }

    private AppConfigurationStore()
    {
        _path = GetConfigPath();
        _configuration = Load();
        _saveTimer = new Timer(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal void Update(Action<AppConfiguration> update)
    {
        lock (_gate)
        {
            update(_configuration);
            _saveTimer.Change(350, Timeout.Infinite);
        }
    }

    internal void Flush()
    {
        _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Save();
    }

    private AppConfiguration Load()
    {
        try
        {
            return File.Exists(_path)
                ? Deserializer.Deserialize<AppConfiguration>(File.ReadAllText(_path)) ?? new AppConfiguration()
                : new AppConfiguration();
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Configuration", $"Could not load {_path}; using defaults", exception);
            return new AppConfiguration();
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, Serializer.Serialize(_configuration));
                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch (Exception exception)
            {
                AppLogger.Warning("Configuration", $"Could not save {_path}", exception);
            }
        }
    }

    private static string GetConfigPath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configRoot, "hyprnetshell", "config.yaml");
    }
}

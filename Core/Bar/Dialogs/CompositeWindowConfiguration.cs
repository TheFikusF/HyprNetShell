using System.Text.Json;
using System.Text.Json.Serialization;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Bar.Dialogs;

internal sealed record CompositeWindowDefinition(
    string Id,
    string Name,
    string Hotkey,
    string[] TabIds);

internal sealed record CompositeWindowConfigurationDocument(CompositeWindowDefinition[] Windows);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CompositeWindowConfigurationDocument))]
internal sealed partial class CompositeWindowConfigurationJsonContext : JsonSerializerContext;

internal sealed class CompositeWindowConfiguration
{
    private readonly string _configPath = GetConfigPath();
    private readonly HashSet<string> _availableTabIds;
    private CompositeWindowDefinition[] _windows;

    internal IReadOnlyList<CompositeWindowDefinition> Windows => _windows;
    internal event Action? Changed;

    internal CompositeWindowConfiguration(IReadOnlyList<IMainDialogTab> tabs)
    {
        _availableTabIds = tabs.Select(tab => tab.Id).ToHashSet(StringComparer.Ordinal);
        _windows = Load(_configPath, tabs);
    }

    internal bool TryUpsert(CompositeWindowDefinition definition, out string error)
    {
        var normalized = Normalize(definition);
        if (normalized.Name.Length == 0)
        {
            error = "Window name is required.";
            return false;
        }

        if (normalized.TabIds.Length == 0)
        {
            error = "Select at least one tab.";
            return false;
        }

        if (normalized.Hotkey.Length > 0 && _windows.Any(window =>
                window.Id != normalized.Id &&
                string.Equals(window.Hotkey, normalized.Hotkey, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"Hotkey '{normalized.Hotkey}' is already assigned.";
            return false;
        }

        var index = Array.FindIndex(_windows, window => window.Id == normalized.Id);
        if (index < 0)
        {
            _windows = [.._windows, normalized];
        }
        else
        {
            var updated = _windows.ToArray();
            updated[index] = normalized;
            _windows = updated;
        }

        Persist();
        Changed?.Invoke();
        error = "";
        return true;
    }

    internal void Delete(string id)
    {
        var updated = _windows.Where(window => window.Id != id).ToArray();
        if (updated.Length == _windows.Length)
        {
            return;
        }

        _windows = updated;
        Persist();
        Changed?.Invoke();
    }

    private CompositeWindowDefinition Normalize(CompositeWindowDefinition definition) => definition with
    {
        Id = string.IsNullOrWhiteSpace(definition.Id) ? Guid.NewGuid().ToString("N") : definition.Id.Trim(),
        Name = definition.Name.Trim(),
        Hotkey = definition.Hotkey.Trim(),
        TabIds = definition.TabIds
            .Where(_availableTabIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
    };

    private CompositeWindowDefinition[] Load(string path, IReadOnlyList<IMainDialogTab> tabs)
    {
        try
        {
            if (File.Exists(path))
            {
                var document = JsonSerializer.Deserialize(
                    File.ReadAllText(path),
                    CompositeWindowConfigurationJsonContext.Default.CompositeWindowConfigurationDocument);
                var windows = document?.Windows
                    .Select(Normalize)
                    .Where(window => window.Name.Length > 0 && window.TabIds.Length > 0)
                    .GroupBy(window => window.Id, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToArray();
                if (windows is { Length: > 0 })
                {
                    return windows;
                }
            }
        }
        catch (Exception exception)
        {
            AppLogger.Warning("CompositeWindows", "Could not load composite window configuration; using defaults", exception);
        }

        return
        [
            new CompositeWindowDefinition(
                Guid.NewGuid().ToString("N"),
                "Launcher",
                "SUPER + R",
                [..tabs.Select(tab => tab.Id)]),
        ];
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            File.WriteAllText(
                _configPath,
                JsonSerializer.Serialize(
                    new CompositeWindowConfigurationDocument(_windows),
                    CompositeWindowConfigurationJsonContext.Default.CompositeWindowConfigurationDocument));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("CompositeWindows", "Could not save composite window configuration", exception);
        }
    }

    private static string GetConfigPath()
    {
        var configRoot = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(configRoot))
        {
            configRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(configRoot, "hyprnetshell", "composite-windows.json");
    }
}

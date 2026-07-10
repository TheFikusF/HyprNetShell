namespace HyprNetShell.Core.Bar;

public sealed class AppIconResolver
{
    private static readonly string[] IconExtensions = [".png", ".svg", ".xpm", ".jpg", ".jpeg", ".webp"];
    private readonly Dictionary<string, string?> _classCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    public string? TryResolve(string className)
    {
        className = className.Trim();
        if (string.IsNullOrWhiteSpace(className))
        {
            return null;
        }

        if (_classCache.TryGetValue(className, out var cached))
        {
            return cached;
        }

        var iconName = FindDesktopIconName(className);
        var path = ResolveIconPath(iconName) ?? ResolveIconPath(className);
        _classCache[className] = path;
        return path;
    }

    private static string? FindDesktopIconName(string className)
    {
        foreach (var applicationsDir in GetApplicationDirectories())
        {
            if (!Directory.Exists(applicationsDir))
            {
                continue;
            }

            foreach (var desktopFile in SafeEnumerateFiles(applicationsDir, "*.desktop"))
            {
                var entry = ReadDesktopEntry(desktopFile);
                if (entry.Count == 0)
                {
                    continue;
                }

                var fileName = Path.GetFileNameWithoutExtension(desktopFile);
                if (Matches(className, fileName) ||
                    Matches(className, GetValue(entry, "StartupWMClass")) ||
                    Matches(className, GetValue(entry, "Name")))
                {
                    return GetValue(entry, "Icon");
                }
            }
        }

        return null;
    }

    private string? ResolveIconPath(string? iconName)
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        iconName = iconName.Trim();
        if (_iconCache.TryGetValue(iconName, out var cached))
        {
            return cached;
        }

        var path = ResolveIconPathCore(iconName);
        _iconCache[iconName] = path;
        return path;
    }

    private static string? ResolveIconPathCore(string iconName)
    {
        if (Path.IsPathRooted(iconName))
        {
            return File.Exists(iconName) ? iconName : null;
        }

        var names = IconExtensions.Contains(Path.GetExtension(iconName), StringComparer.OrdinalIgnoreCase)
            ? new[] { iconName }
            : IconExtensions.Select(extension => iconName + extension).ToArray();

        foreach (var iconDir in GetIconDirectories())
        {
            if (!Directory.Exists(iconDir))
            {
                continue;
            }

            foreach (var name in names)
            {
                var direct = Path.Combine(iconDir, name);
                if (File.Exists(direct))
                {
                    return direct;
                }
            }

            foreach (var file in SafeEnumerateFiles(iconDir, "*", SearchOption.AllDirectories))
            {
                if (names.Any(name => string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase)))
                {
                    return file;
                }
            }
        }

        return null;
    }

    private static Dictionary<string, string> ReadDesktopEntry(string desktopFile)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var line in File.ReadLines(desktopFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == '[')
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                values[trimmed[..separator]] = trimmed[(separator + 1)..];
            }
        }
        catch
        {
            values.Clear();
        }

        return values;
    }

    private static string? GetValue(Dictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }

    private static bool Matches(string className, string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               string.Equals(Normalize(className), Normalize(value), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    private static IEnumerable<string> GetApplicationDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            yield return Path.Combine(dataHome, "applications");
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".local/share/applications");
        }

        foreach (var dataDir in GetDataDirectories())
        {
            yield return Path.Combine(dataDir, "applications");
        }
    }

    private static IEnumerable<string> GetIconDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(dataHome))
        {
            yield return Path.Combine(dataHome, "icons");
        }

        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".local/share/icons");
            yield return Path.Combine(home, ".icons");
        }

        foreach (var dataDir in GetDataDirectories())
        {
            yield return Path.Combine(dataDir, "icons");
        }

        yield return "/usr/share/pixmaps";
    }

    private static IEnumerable<string> GetDataDirectories()
    {
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        return string.IsNullOrWhiteSpace(dataDirs)
            ? new[] { "/usr/local/share", "/usr/share" }
            : dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<string> SafeEnumerateFiles(
        string path,
        string pattern,
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        try
        {
            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = searchOption == SearchOption.AllDirectories,
            };

            return Directory.EnumerateFiles(path, pattern, options).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace HyprNetShell.Core.Bar;

public sealed class AppIconResolver
{
    private static readonly string[] IconExtensions = [".png", ".svg", ".svgz", ".xpm", ".jpg", ".jpeg", ".webp"];
    private static readonly string[] RasterIconExtensions = [".png", ".xpm", ".jpg", ".jpeg", ".webp"];
    private static readonly Lazy<string?> SafeSvgCacheDirectory = new(CreateSafeSvgCacheDirectory);
    private static readonly HashSet<string> SafeSvgElements = new(StringComparer.Ordinal)
    {
        "svg", "g", "defs", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
        "linearGradient", "radialGradient", "stop", "clipPath", "use",
    };
    private readonly Dictionary<string, string?> _classCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _rasterIconCache = new(StringComparer.OrdinalIgnoreCase);

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

    public string? TryResolveIcon(string iconName) => ResolveIconPath(iconName);

    public string? TryResolveRasterIcon(string iconName)
    {
        iconName = iconName.Trim();
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        if (_rasterIconCache.TryGetValue(iconName, out var cached))
        {
            return cached;
        }

        var path = ResolveIconPathCore(iconName, RasterIconExtensions);
        _rasterIconCache[iconName] = path;
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

        var path = MakeRendererSafe(ResolveIconPathCore(iconName, IconExtensions));
        _iconCache[iconName] = path;
        return path;
    }

    private static string? MakeRendererSafe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            (!Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase) &&
             !Path.GetExtension(path).Equals(".svgz", StringComparison.OrdinalIgnoreCase)))
        {
            return path;
        }

        // Svg.Skia can terminate the process on unsupported constructs in old
        // third-party Inkscape files (notably flowRoot/text). Reduce external
        // icons to the geometry subset used by our known-safe bundled assets.
        if (Path.GetExtension(path).Equals(".svgz", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var cacheDirectory = SafeSvgCacheDirectory.Value;
        if (cacheDirectory is null)
        {
            return null;
        }

        try
        {
            var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{Path.GetFullPath(path)}|{File.GetLastWriteTimeUtc(path).Ticks}")));
            var safePath = Path.Combine(cacheDirectory, cacheKey + ".svg");
            if (File.Exists(safePath))
            {
                return safePath;
            }

            var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root is null || root.Name.LocalName != "svg")
            {
                return null;
            }

            foreach (var element in root.DescendantsAndSelf().ToArray())
            {
                if (!SafeSvgElements.Contains(element.Name.LocalName))
                {
                    element.Remove();
                    continue;
                }

                foreach (var attribute in element.Attributes().ToArray())
                {
                    var name = attribute.Name.LocalName;
                    var externalReference = name == "href" && !attribute.Value.StartsWith('#');
                    var unsafeStyle = name == "style" &&
                                      attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase) &&
                                      !attribute.Value.Contains("url(#", StringComparison.OrdinalIgnoreCase);
                    if ((!attribute.IsNamespaceDeclaration && !string.IsNullOrEmpty(attribute.Name.NamespaceName)) ||
                        name.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
                        externalReference || unsafeStyle)
                    {
                        attribute.Remove();
                    }
                }
            }

            document.Save(safePath, SaveOptions.DisableFormatting);
            return File.Exists(safePath) ? safePath : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? CreateSafeSvgCacheDirectory()
    {
        try
        {
            return Directory.CreateTempSubdirectory("hyprnetshell-safe-svg-").FullName;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveIconPathCore(string iconName, IReadOnlyCollection<string> extensions)
    {
        if (Uri.TryCreate(iconName, UriKind.Absolute, out var iconUri) && iconUri.IsFile)
        {
            iconName = iconUri.LocalPath;
        }

        if (Path.IsPathRooted(iconName))
        {
            return File.Exists(iconName) ? iconName : null;
        }

        var suppliedExtension = Path.GetExtension(iconName);
        if (!string.IsNullOrEmpty(suppliedExtension) &&
            !extensions.Contains(suppliedExtension, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var names = !string.IsNullOrEmpty(suppliedExtension)
            ? new[] { iconName }
            : extensions.Select(extension => iconName + extension).ToArray();

        foreach (var iconDir in GetIconDirectories())
        {
            if (!Directory.Exists(iconDir))
            {
                continue;
            }

            var candidates = new List<string>();
            foreach (var name in names)
            {
                var direct = Path.Combine(iconDir, name);
                if (File.Exists(direct))
                {
                    candidates.Add(direct);
                }
            }

            var expectedNames = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
            candidates.AddRange(
                SafeEnumerateFiles(iconDir, "*", SearchOption.AllDirectories)
                    .Where(file => expectedNames.Contains(Path.GetFileName(file))));

            if (candidates.Count > 0)
            {
                return candidates
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(IconCandidateScore)
                    .ThenBy(path => path, StringComparer.Ordinal)
                    .First();
            }
        }

        return null;
    }

    private static int IconCandidateScore(string path)
    {
        var normalized = path.Replace('\\', '/');
        var score = normalized.Contains("/apps/", StringComparison.OrdinalIgnoreCase) ? 0 : 1000;
        if (normalized.Contains("symbolic", StringComparison.OrdinalIgnoreCase))
        {
            score += 500;
        }

        var size = IconSizeFromPath(normalized);
        score += size switch
        {
            48 => 0,
            64 => 2,
            32 => 4,
            96 => 6,
            128 => 8,
            256 => 10,
            512 => 12,
            > 0 => 20 + Math.Abs(size - 48),
            _ when normalized.Contains("/scalable/", StringComparison.OrdinalIgnoreCase) => 1,
            _ => 100,
        };
        return score;
    }

    private static int IconSizeFromPath(string path)
    {
        foreach (var segment in path.Split('/'))
        {
            var separator = segment.IndexOf('x');
            if (separator > 0 &&
                int.TryParse(segment[..separator], out var width) &&
                int.TryParse(segment[(separator + 1)..], out var height) &&
                width == height)
            {
                return width;
            }
        }

        return 0;
    }

    private static Dictionary<string, string> ReadDesktopEntry(string desktopFile)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var inDesktopEntry = false;

        try
        {
            foreach (var line in File.ReadLines(desktopFile))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('['))
                {
                    inDesktopEntry = string.Equals(
                        trimmed,
                        "[Desktop Entry]",
                        StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inDesktopEntry || trimmed.Length == 0 || trimmed[0] == '#')
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

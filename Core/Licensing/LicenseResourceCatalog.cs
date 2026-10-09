using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Licensing;

internal static class LicenseResourceCatalog
{
    internal sealed record Notice(string ResourceName, string Path, string Title);

    private const string RESOURCE_PREFIX = "HyprNetShell.Licenses/";

    private static readonly System.Reflection.Assembly ResourceAssembly = typeof(LicenseResourceCatalog).Assembly;

    public static IReadOnlyList<Notice> Notices { get; } = BuildNotices();

    public static string Read(Notice notice)
    {
        try
        {
            using var stream = ResourceAssembly.GetManifestResourceStream(notice.ResourceName);
            if (stream is null)
            {
                AppLogger.Warning("Licenses", $"Embedded license resource '{notice.ResourceName}' is missing; rebuild with assets/licenses included");
                return $"License notice unavailable: {notice.Path}";
            }

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        catch (Exception exception)
        {
            AppLogger.Warning("Licenses", $"Could not read embedded license resource '{notice.ResourceName}'", exception);
            return $"Could not read license notice: {notice.Path}";
        }
    }

    private static IReadOnlyList<Notice> BuildNotices()
    {
        var notices = ResourceAssembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(RESOURCE_PREFIX, StringComparison.Ordinal))
            .Select(name =>
            {
                var path = name[RESOURCE_PREFIX.Length..];
                return new Notice(name, path, BuildTitle(path));
            })
            .OrderBy(notice => notice.Path == "LICENSE" ? 0 : 1)
            .ThenBy(notice => notice.Title, StringComparer.Ordinal)
            .ToArray();

        if (notices.Length == 0)
        {
            AppLogger.Warning("Licenses", "No embedded license notices found; rebuild with assets/licenses included");
        }

        return notices;
    }

    private static string BuildTitle(string path)
    {
        if (path == "LICENSE")
        {
            return "HyprNetShell — MIT License";
        }

        var parts = path.Split('/');
        var component = parts.Length > 1 ? parts[1] : parts[0];
        if (parts[0] is "fonts" or "icons")
        {
            var assetName = component switch {
                "0xProto-LICENSE" => "0xProto NL — SIL Open Font License 1.1",
                "LiberationMono-LICENSE" => "Liberation Mono — SIL Open Font License 1.1",
                "NotoColorEmoji-LICENSE" => "Noto Color Emoji — SIL Open Font License 1.1 (upstream root)",
                "NotoColorEmoji-fonts-LICENSE" => "Noto Color Emoji — SIL Open Font License 1.1 (fonts)",
                "NotoColorEmoji-COPYRIGHT" => "Noto Color Emoji — bundled font copyright and trademark",
                "lucide-LICENSE" => "Lucide icons — ISC and Feather MIT notices",
                _ => string.Join("/", parts.Skip(1)),
            };
            return assetName;
        }
        var name = component switch {
            "skia" => "Skia",
            "harfbuzz" => "HarfBuzz",
            "sqlite" => "SQLite (public domain)",
            _ => component,
        };
        var relativePath = string.Join("/", parts.Skip(2));
        var format = path.EndsWith(".rtf", StringComparison.OrdinalIgnoreCase) ? " (raw RTF)" : "";
        return $"{name} — {relativePath}{format}";
    }
}

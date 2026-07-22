namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed record DesktopApplication(
    string DesktopId,
    string Name,
    string? Comment,
    string? Icon,
    string DesktopFile,
    IReadOnlyList<DesktopAction> Actions);

internal sealed record DesktopAction(
    string Id,
    string Name,
    string? Icon,
    string Exec);

internal static class DesktopApplicationParser
{
    public static DesktopApplication? Parse(string path)
    {
        try
        {
            var groups = ReadGroups(path);
            if (!groups.TryGetValue("Desktop Entry", out var entry) ||
                !ValueIs(entry, "Type", "Application") ||
                ValueIs(entry, "Hidden", "true") ||
                ValueIs(entry, "NoDisplay", "true") ||
                !entry.TryGetValue("Name", out var name) ||
                string.IsNullOrWhiteSpace(name) ||
                !entry.ContainsKey("Exec"))
            {
                return null;
            }

            return new DesktopApplication(
                Path.GetFileNameWithoutExtension(path),
                Unescape(name),
                entry.TryGetValue("Comment", out var comment) ? Unescape(comment) : null,
                entry.GetValueOrDefault("Icon"),
                path,
                ParseActions(entry, groups));
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> ReadGroups(string path)
    {
        var groups = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        Dictionary<string, string>? currentGroup = null;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var groupName = line[1..^1];
                if (!groups.TryGetValue(groupName, out currentGroup))
                {
                    currentGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    groups[groupName] = currentGroup;
                }
                continue;
            }

            if (currentGroup is null || line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                currentGroup[line[..separator]] = line[(separator + 1)..].Trim();
            }
        }

        return groups;
    }

    private static IReadOnlyList<DesktopAction> ParseActions(
        IReadOnlyDictionary<string, string> entry,
        IReadOnlyDictionary<string, Dictionary<string, string>> groups)
    {
        if (!entry.TryGetValue("Actions", out var actionList))
        {
            return [];
        }

        var actions = new List<DesktopAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in actionList.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!seen.Add(id) ||
                !groups.TryGetValue($"Desktop Action {id}", out var values) ||
                !values.TryGetValue("Name", out var name) ||
                string.IsNullOrWhiteSpace(name) ||
                !values.TryGetValue("Exec", out var exec) ||
                string.IsNullOrWhiteSpace(exec))
            {
                continue;
            }

            actions.Add(new DesktopAction(
                id,
                Unescape(name),
                values.GetValueOrDefault("Icon"),
                exec));
        }

        return actions;
    }

    private static bool ValueIs(IReadOnlyDictionary<string, string> values, string key, string expected) =>
        values.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string Unescape(string value) => value
        .Replace("\\s", " ", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\t", "\t", StringComparison.Ordinal)
        .Replace("\\r", "\r", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);
}

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

    private static List<DesktopAction> ParseActions(
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

            actions.Add(new DesktopAction(id, Unescape(name), values.GetValueOrDefault("Icon"), exec));
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

internal sealed class DesktopApplicationCatalog : IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _refreshLoop;
    private IReadOnlyList<DesktopApplication> _applications = [];
    private CatalogStamp _stamp;
    private bool _loaded;

    internal DesktopApplicationCatalog()
    {
        _refreshLoop = Task.Run(() => RefreshLoopAsync(_lifetime.Token));
    }

    internal event Action? Changed;

    internal IReadOnlyList<DesktopApplication> Snapshot
    {
        get
        {
            lock (_stateLock)
            {
                return _applications;
            }
        }
    }

    internal void RefreshSoon() => _ = Task.Run(() => RefreshAsync(force: false, _lifetime.Token));

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(force: true, cancellationToken);
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RefreshAsync(force: false, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            var scan = await Task.Run(ScanFiles, cancellationToken);
            lock (_stateLock)
            {
                if (!force && _loaded && scan.Stamp == _stamp)
                {
                    return;
                }
            }

            var applications = await Task.Run(() => LoadApplications(scan.Files, cancellationToken), cancellationToken);
            lock (_stateLock)
            {
                _applications = applications;
                _stamp = scan.Stamp;
                _loaded = true;
            }

            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // A transient filesystem error must not clear the last usable application snapshot.
        }
    }

    private static DesktopApplication[] LoadApplications(IReadOnlyList<string> files, CancellationToken cancellationToken)
    {
        var applications = new Dictionary<string, DesktopApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var desktopFile in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var application = DesktopApplicationParser.Parse(desktopFile);
            if (application is not null)
            {
                applications.TryAdd(application.DesktopId, application);
            }
        }

        return applications.Values
            .OrderBy(application => application.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static CatalogScan ScanFiles()
    {
        var files = new List<string>();
        var hash = new HashCode();
        foreach (var directory in ApplicationDirectories().Where(Directory.Exists))
        {
            foreach (var file in SafeDesktopFiles(directory))
            {
                files.Add(file);
                try
                {
                    var info = new FileInfo(file);
                    hash.Add(file, StringComparer.Ordinal);
                    hash.Add(info.LastWriteTimeUtc.Ticks);
                    hash.Add(info.Length);
                }
                catch
                {
                    hash.Add(file, StringComparer.Ordinal);
                }
            }
        }

        return new CatalogScan(files, new CatalogStamp(files.Count, hash.ToHashCode()));
    }

    private static IEnumerable<string> ApplicationDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        yield return !string.IsNullOrWhiteSpace(dataHome)
            ? Path.Combine(dataHome, "applications")
            : Path.Combine(home, ".local/share/applications");

        var dataDirectories = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var directory in string.IsNullOrWhiteSpace(dataDirectories)
                     ? new[] { "/usr/local/share", "/usr/share" }
                     : dataDirectories.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(directory, "applications");
        }
    }

    private static IEnumerable<string> SafeDesktopFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.desktop", new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
            }).Order(StringComparer.Ordinal).ToArray();
        }
        catch
        {
            return [];
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private readonly record struct CatalogStamp(int FileCount, int Hash);
    private sealed record CatalogScan(IReadOnlyList<string> Files, CatalogStamp Stamp);
}

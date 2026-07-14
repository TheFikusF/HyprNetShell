using System.Diagnostics;
using HyprNetShell.Core.Assets;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ApplicationLauncherTab(Action closeDialog) : IMainDialogTab
{
    private readonly AppIconResolver _icons = new();
    private IReadOnlyList<DesktopApplication> _applications = [];
    private IReadOnlyList<DesktopApplication> _filteredApplications = [];
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;

    public string Title => "Applications";
    public SvgAsset Icon => Icons.Application;

    public void Activate()
    {
        if (_applications.Count == 0)
        {
            _applications = LoadApplications();
        }

        _firstIndex = 0;
        _selectedIndex = 0;
        ApplyFilter();
    }

    public void HandleTextInput(string text)
    {
        _query += text;
        ApplyFilter();
    }

    public void HandleBackspace()
    {
        if (_query.Length > 0)
        {
            _query = MainDialogTabUi.RemoveLastTextElement(_query);
            ApplyFilter();
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            MainDialogTabUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1,
                _filteredApplications.Count);
        }
    }

    public void ActivateSelection()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _filteredApplications.Count)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "gio",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "launch", _filteredApplications[_selectedIndex].DesktopFile },
            });
            closeDialog();
        }
        catch
        {
            // Keep the launcher open when the desktop entry cannot be started.
        }
    }

    public Node Draw()
    {
        var visibleApps = _filteredApplications
            .Skip(_firstIndex)
            .Take(MainDialogTabUi.VisibleRowCount)
            .ToArray();
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Applications",
                    MainDialogTabUi.ResultCount(
                        _selectedIndex,
                        _filteredApplications.Count,
                        "No matching applications")),
                MainDialogTabUi.BuildInput(_query, "Type to search..."),
                ..visibleApps.Select((app, visibleIndex) => BuildRow(app, _firstIndex + visibleIndex)),
            ],
        };
    }

    private Node BuildRow(DesktopApplication app, int index)
    {
        var selected = index == _selectedIndex;
        var iconPath = string.IsNullOrWhiteSpace(app.Icon) ? null : _icons.TryResolveRasterIcon(app.Icon);
        return new BoxNode(height: 66)
        {
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                _selectedIndex = index;
                ActivateSelection();
            },
            Style = ModulesCommon.ModuleStyle(
                    Theme.Default,
                    selected ? Theme.Default.Active : Theme.Default.Panel) with
                {
                    BorderRadius = 8,
                    BorderWidth = selected ? Theme.Default.BorderWidth : 0,
                    Padding = new Insets(16, 10),
                    Spacing = 14,
                },
            Children =
            [
                iconPath is not null
                    ? new ImageNode(iconPath, 38, 38)
                    : new ImageNode(Icons.Application, 38, 38, Theme.Default.Text),
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 3 },
                    Children =
                    [
                        new TextNode(MainDialogTabUi.Trim(app.Name, 58), 17, Theme.Default.Text),
                        new TextNode(MainDialogTabUi.Trim(app.Comment ?? app.DesktopId, 76), 14,
                            Theme.Default.Text),
                    ],
                },
            ],
        };
    }

    private void ApplyFilter()
    {
        _filteredApplications = string.IsNullOrWhiteSpace(_query)
            ? _applications
            : _applications
                .Where(app => app.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        _firstIndex = 0;
        _selectedIndex = 0;
    }

    private static IReadOnlyList<DesktopApplication> LoadApplications()
    {
        var applications = new Dictionary<string, DesktopApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in ApplicationDirectories().Where(Directory.Exists))
        {
            foreach (var desktopFile in SafeDesktopFiles(directory))
            {
                var app = ParseDesktopFile(desktopFile);
                if (app is not null)
                {
                    applications.TryAdd(app.DesktopId, app);
                }
            }
        }

        return applications.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static DesktopApplication? ParseDesktopFile(string path)
    {
        try
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inDesktopEntry = false;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.StartsWith('['))
                {
                    inDesktopEntry = string.Equals(line, "[Desktop Entry]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inDesktopEntry || line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator > 0)
                {
                    values[line[..separator]] = line[(separator + 1)..].Trim();
                }
            }

            if (!ValueIs(values, "Type", "Application") ||
                ValueIs(values, "Hidden", "true") ||
                ValueIs(values, "NoDisplay", "true") ||
                !values.TryGetValue("Name", out var name) ||
                string.IsNullOrWhiteSpace(name) ||
                !values.ContainsKey("Exec"))
            {
                return null;
            }

            return new DesktopApplication(
                Path.GetFileNameWithoutExtension(path),
                Unescape(name),
                values.TryGetValue("Comment", out var comment) ? Unescape(comment) : null,
                values.GetValueOrDefault("Icon"),
                path);
        }
        catch
        {
            return null;
        }
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
                     : dataDirectories.Split(':',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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
            }).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool ValueIs(IReadOnlyDictionary<string, string> values, string key, string expected) =>
        values.TryGetValue(key, out var value) && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    private static string Unescape(string value) => value
        .Replace("\\s", " ", StringComparison.Ordinal)
        .Replace("\\n", "\n", StringComparison.Ordinal)
        .Replace("\\t", "\t", StringComparison.Ordinal)
        .Replace("\\r", "\r", StringComparison.Ordinal)
        .Replace("\\\\", "\\", StringComparison.Ordinal);

    private sealed record DesktopApplication(
        string DesktopId,
        string Name,
        string? Comment,
        string? Icon,
        string DesktopFile);
}
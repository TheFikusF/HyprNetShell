using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using FuzzySharp;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ApplicationLauncherTab(IHyprctl hyprctl, Action closeDialog, Theme theme) : IMainDialogTab
{
    private const int FUZZY_SCORE_CUTOFF = 35;
    private readonly AppIconResolver _icons = new();
    private readonly Dictionary<int, ModulesCommon.BoxState> _buttonsState = new();
    private IReadOnlyList<DesktopApplication> _applications = [];
    private IReadOnlyList<DesktopApplication> _filteredApplications = [];
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;
    private bool _launching;

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
        if (_launching || _selectedIndex < 0 || _selectedIndex >= _filteredApplications.Count)
        {
            return;
        }

        _launching = true;
        _ = LaunchAsync(_filteredApplications[_selectedIndex].DesktopFile);
    }

    private async Task LaunchAsync(string desktopFile)
    {
        try
        {
            if (await hyprctl.LaunchDesktopEntryAsync(desktopFile))
            {
                closeDialog();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("ApplicationLauncher", $"Could not launch desktop entry {desktopFile}", exception);
        }
        finally
        {
            _launching = false;
        }
    }

    public Node Draw() => new BoxNode
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
            MainDialogTabUi.BuildScrollableResults(
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    Style = new Style { Spacing = 8 },
                    Children = _filteredApplications
                        .Skip(_firstIndex)
                        .Take(MainDialogTabUi.VISIBLE_ROW_COUNT)
                        .Select((app, visibleIndex) => BuildRow(app, _firstIndex + visibleIndex))
                        .ToArray(),
                },
                _firstIndex,
                _filteredApplications.Count,
                MainDialogTabUi.VISIBLE_ROW_COUNT),
        ],
    };

    private Node BuildRow(DesktopApplication app, int index)
    {
        var selected = index == _selectedIndex;
        var iconPath = string.IsNullOrWhiteSpace(app.Icon) ? null : _icons.TryResolveIcon(app.Icon);
        var state = _buttonsState.GetState(index, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
        return new BoxNode(height: 66)
        {
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                _selectedIndex = index;
                ActivateSelection();
            },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(Theme.Default, state.Background) with
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
                .Select(app => (App: app,
                    Score: PrimitivesMath.Lerp(Fuzz.WeightedRatio(_query, app.Name),
                        Fuzz.WeightedRatio(_query, app.Comment ?? ""), 0.15f)))
                .Where(result => result.Score >= FUZZY_SCORE_CUTOFF)
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(result => result.App)
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
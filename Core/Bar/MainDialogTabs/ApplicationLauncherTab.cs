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
    private class ButtonState : ModulesCommon.BoxState
    {
        public int ActionIndex { get; set; }
        public Dictionary<int, ModulesCommon.BoxState> Actions { get; } = new ();
    }

    private enum Column
    {
        Default,
        Actions
    }

    private const int FUZZY_SCORE_CUTOFF = 35;
    private readonly AppIconResolver _icons = new();
    private readonly Dictionary<int, ButtonState> _buttonsState = new();
    private IReadOnlyList<DesktopApplication> _applications = [];
    private IReadOnlyList<DesktopApplication> _filteredApplications = [];
    private string _query = "";
    private int _firstIndex;

    private int _selectedIndex;
    private Column _selectedColumn;

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
            
            return;
        }

        if (_launching || _selectedIndex < 0 || _selectedIndex >= _filteredApplications.Count)
        {
            return;
        }

        var state = _buttonsState[_selectedIndex];
        var desktopEntry = _filteredApplications[_selectedIndex];

        if (_selectedColumn == Column.Actions)
        {
            state.ActionIndex += direction == SelectionDirection.Right ? 1 : -1;
        }

        (state.ActionIndex, _selectedColumn) = direction switch
        {
            SelectionDirection.Right when _selectedColumn is Column.Actions && state.ActionIndex >= desktopEntry.Actions.Count => (0, Column.Default),
            SelectionDirection.Left when _selectedColumn is Column.Actions && state.ActionIndex < 0 => (0, Column.Default),
            SelectionDirection.Left when _selectedColumn is Column.Default => (desktopEntry.Actions.Count - 1, Column.Actions),
            SelectionDirection.Right when _selectedColumn is Column.Default => (0, Column.Actions),
            _ => (state.ActionIndex, _selectedColumn),
        };
    }

    public void ActivateSelection()
    {
        if (_launching || _selectedIndex < 0 || _selectedIndex >= _filteredApplications.Count)
        {
            return;
        }

        var application = _filteredApplications[_selectedIndex];
        var state = _buttonsState[_selectedIndex];
        _launching = true;

        _ = _selectedColumn == Column.Default 
            ? LaunchAsync(application) 
            : LaunchActionAsync(application, application.Actions[state.ActionIndex]);
    }

    private async Task LaunchAsync(DesktopApplication application)
    {
        try
        {
            if (await hyprctl.LaunchDesktopEntryAsync(application.DesktopFile))
            {
                _query = "";
                ApplyFilter();
                closeDialog();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error("ApplicationLauncher", $"Could not launch desktop entry {application.DesktopFile}", exception);
        }
        finally
        {
            _launching = false;
        }
    }

    private async Task LaunchActionAsync(DesktopApplication application, DesktopAction action)
    {
        try
        {
            if (await hyprctl.LaunchDesktopActionAsync(application.DesktopFile, action.Id))
            {
                _query = "";
                ApplyFilter();
                closeDialog();
            }
        }
        catch (Exception exception)
        {
            AppLogger.Error(
                "ApplicationLauncher",
                $"Could not launch desktop action {action.Id} from {application.DesktopFile}",
                exception);
        }
        finally
        {
            _launching = false;
        }
    }

    public Node Draw() => new BoxNode(new Style { Spacing = 8 })
    {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
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

    private BoxNode BuildRow(DesktopApplication app, int index)
    {
        var selected = index == _selectedIndex;
        var actionSelected = selected && app.Actions.Count > 0 && _selectedColumn == Column.Actions;
        var entrySelected = selected && !actionSelected;

        var iconPath = string.IsNullOrWhiteSpace(app.Icon) ? null : _icons.TryResolveIcon(app.Icon);
        var state = _buttonsState.GetState(index, theme.Panel).UpdateColor(entrySelected ? theme.Active : theme.Panel);
        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style
            {
                Spacing = 8
            },
            Children =
            [
                new BoxNode(height: 66)
                {
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Center,
                    OnClick = () =>
                    {
                        _selectedIndex = index;
                        ActivateSelection();
                    },
                    IsHovered = state.Hovered,
                    Style = ModulesCommon.ModuleStyle(theme, state.Background) with
                    {
                        BorderRadius = 8,
                        BorderWidth = selected ? theme.BorderWidth : 0,
                        Padding = new Insets(16, 10),
                        Spacing = 14,
                    },
                    Children =
                    [
                        iconPath is not null
                            ? new ImageNode(iconPath, 38, 38)
                            : new ImageNode(Icons.Application, 38, 38, theme.Text),
                        new BoxNode
                        {
                            Direction = Direction.Vertical,
                            VerticalAlignment = ItemsAlignment.Center,
                            Style = new Style { Spacing = 3 },
                            Children =
                            [
                                new TextNode(app.Name, 18, theme.Text),
                                new TextNode(app.Comment ?? "", theme.TextSize, theme.Text),
                            ],
                        },
                    ],
                },
                ..app.Actions.Select((x, i) => BuildAction(actionSelected, i, state, selected, x))
            ]
        };
    }

    private BoxNode BuildAction(bool actionsSelected, int i, ButtonState entryState, bool selected, DesktopAction x)
    {
        var actionSelected = actionsSelected && i == entryState.ActionIndex;
        var state = entryState.Actions.GetState(i, theme.Panel).UpdateColor(actionSelected ? theme.Active : theme.Panel);
        return new BoxNode(actionSelected ? null : 32, 66)
        {
            // OnClick = () =>
            // {
            //     _selectedIndex = index;
            //     ActivateSelection();
            // },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.BorderWidth : 0,
                Padding = actionSelected ? new Insets(16, 10) : new Insets(4, 10),
                Spacing = 8,
            },
            Children =
            [
                new BoxNode(16, 16)
                {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { BackgroundColor = Color.Black, BorderRadius = new BorderRadius(theme.BorderRadius) },
                    Children = { new TextNode((i + 1).ToString(), 14, theme.Text) },
                },
                actionSelected ? new TextNode(x.Name, theme.TextSize, theme.Text) : new SpacerNode(),
            ]
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

    private static DesktopApplication[] LoadApplications()
    {
        var applications = new Dictionary<string, DesktopApplication>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in ApplicationDirectories().Where(Directory.Exists))
        {
            foreach (var desktopFile in SafeDesktopFiles(directory))
            {
                var app = DesktopApplicationParser.Parse(desktopFile);
                if (app is not null)
                {
                    applications.TryAdd(app.DesktopId, app);
                }
            }
        }

        return applications.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
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
}
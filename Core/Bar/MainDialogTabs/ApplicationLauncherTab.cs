using HyprNetShell.GUI;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.Hyprland;

using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Core.Bar.Common;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ApplicationLauncherTab(
    IHyprctl hyprctl,
    Action closeDialog,
    TextInputCoordinator inputs) : IMainDialogTab, IDisposable
{
    private const int FUZZY_SCORE_CUTOFF = 35;
    private readonly DesktopApplicationCatalog _catalog = new();
    private readonly ApplicationResultInteraction _applicationResults = new();
    private readonly TextInputCoordinator.Input _queryInput = inputs.Create(
        "",
        "",
        "Type to search...",
        4096,
        alwaysActive: true);
    private IReadOnlyList<DesktopApplication> _applications = [];
    private IReadOnlyList<DesktopApplication> _filteredApplications = [];
    private int _firstIndex;

    private int _selectedIndex;
    private bool _launching;

    public string Id => "applications";
    public string Title => "Applications";
    public SvgAsset Icon => Icons.Application;

    public void Activate()
    {
        inputs.Configure(_queryInput, _ => ApplyFilter());
        inputs.Activate(_queryInput);
        _catalog.RefreshSoon();
        UpdateApplications();
        _firstIndex = 0;
        _selectedIndex = 0;
        ApplyFilter();
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            BoundedListUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1,
                _filteredApplications.Count);

            NormalizeActionSelection();
            return;
        }

        if (_launching || _selectedIndex < 0 || _selectedIndex >= _filteredApplications.Count)
        {
            return;
        }

        _applicationResults.MoveHorizontal(
            _selectedIndex,
            _filteredApplications[_selectedIndex],
            direction);
    }

    public void ActivateSelection()
    {
        if (_launching || _selectedIndex < 0 || _selectedIndex >= _filteredApplications.Count)
        {
            return;
        }

        var application = _filteredApplications[_selectedIndex];
        var action = _applicationResults.SelectedAction(_selectedIndex, application);
        _launching = true;
        _ = LaunchAsync(application, action);
    }

    private async Task LaunchAsync(DesktopApplication application, DesktopAction? action)
    {
        try
        {
            if (await DesktopApplicationLaunch.TryLaunchAsync(
                    hyprctl,
                    application,
                    action,
                    "ApplicationLauncher"))
            {
                inputs.SetValue(_queryInput, "", notify: true);
                closeDialog();
            }
        }
        finally
        {
            _launching = false;
        }
    }

    public Node Draw()
    {
        UpdateApplications();
        return new BoxNode(new Style { Spacing = 8 }) {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Applications",
                    MainDialogTabUi.ResultCount(
                        _selectedIndex,
                        _filteredApplications.Count,
                        _applications.Count == 0 ? "Loading applications..." : "No matching applications")),
                inputs.Build(_queryInput),
                BoundedListUi.BuildScrollableResults(
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = new Style { Spacing = 8 },
                        Children = _filteredApplications
                            .VisibleItems(_firstIndex)
                            .Select(item => BuildRow(item.Item, item.Index))
                            .ToArray(),
                    },
                    _firstIndex,
                    _filteredApplications.Count,
                    BoundedListUi.DEFAULT_VISIBLE_ITEM_COUNT),
            ],
        };
    }

    private BoxNode BuildRow(DesktopApplication application, int index) =>
        _applicationResults.BuildRow(
            application,
            index,
            index == _selectedIndex,
            () =>
            {
                _selectedIndex = index;
                ActivateSelection();
            },
            _ =>
            {
                _selectedIndex = index;
                ActivateSelection();
            });

    internal static BoxNode BuildApplicationRow(
        DesktopApplication application,
        bool selected,
        ApplicationSelectionColumn selectedColumn,
        ApplicationButtonState entryState,
        AppIconResolver icons, Action activateDefault,
        Action<int> activateAction)
    {
        var actionsSelected = selected &&
                              application.Actions.Count > 0 &&
                              selectedColumn == ApplicationSelectionColumn.Actions;
        var entrySelected = selected && !actionsSelected;
        var iconPath = string.IsNullOrWhiteSpace(application.Icon)
            ? null
            : icons.TryResolveIcon(application.Icon);
        entryState.UpdateColor(entrySelected ? ThemeManager.Current.Active : ThemeManager.Current.Panel);

        return new BoxNode {
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                new BoxNode(height: 66)
                {
                    HorizontalAlignment = ItemsAlignment.Stretch,
                    VerticalAlignment = ItemsAlignment.Center,
                    OnClick = activateDefault,
                    IsHovered = entryState.Hovered,
                    Style = ModulesCommon.ModuleStyle(entryState.Background) with
                    {
                        BorderRadius = 8,
                        BorderWidth = selected ? ThemeManager.Current.Border.Width : 0,
                        Padding = new Insets(16, 10),
                        Spacing = 14,
                    },
                    Children =
                    [
                        iconPath is not null
                            ? new ImageNode(iconPath, 38, 38)
                            : new ImageNode(Icons.Application, 38, 38, ThemeManager.Current.Text),
                        new BoxNode
                        {
                            Direction = Direction.Vertical,
                            VerticalAlignment = ItemsAlignment.Center,
                            Style = new Style { Spacing = 3 },
                            Children =
                            [
                                new TextNode(application.Name, 18),
                                new TextNode(application.Comment ?? "", color: entrySelected ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor),
                            ],
                        },
                    ],
                },
                ..application.Actions.Select((action, actionIndex) => BuildApplicationAction(
                    action,
                    actionIndex,
                    actionsSelected,
                    selected,
                    entryState, () => activateAction(actionIndex))),
            ],
        };
    }

    private static BoxNode BuildApplicationAction(
        DesktopAction action,
        int actionIndex,
        bool actionsSelected,
        bool entrySelected,
        ApplicationButtonState entryState, Action activate)
    {
        var selected = actionsSelected && actionIndex == entryState.ActionIndex;
        var state = entryState.Actions
            .GetState(actionIndex, ThemeManager.Current.Panel)
            .UpdateColor(selected ? ThemeManager.Current.Active : ThemeManager.Current.Panel);
        return new BoxNode(selected ? null : 32, 66) {
            OnClick = activate,
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                BorderRadius = 8,
                BorderWidth = entrySelected ? ThemeManager.Current.Border.Width : 0,
                Padding = selected ? new Insets(16, 10) : new Insets(4, 10),
                Spacing = 8,
            },
            Children =
            [
                new BoxNode(16, 16)
                {
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style
                    {
                        BackgroundColor = Color.Black,
                        BorderRadius = new BorderRadius(ThemeManager.Current.Border.Radius),
                    },
                    Children = [new TextNode((actionIndex + 1).ToString(), 14)],
                },
                selected ? new TextNode(action.Name) : new SpacerNode(),
            ],
        };
    }

    private void ApplyFilter()
    {
        var query = _queryInput.Value;
        _filteredApplications = string.IsNullOrWhiteSpace(query)
            ? _applications
            : _applications
                .Select(app => (App: app,
                    Score: Math.Max(
                        FuzzySearch.Score(query, app.Name),
                        FuzzySearch.Score(query, app.Comment ?? "") - 12)))
                .Where(result => result.Score >= FUZZY_SCORE_CUTOFF)
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.App.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(result => result.App)
                .ToArray();
        _firstIndex = 0;
        _selectedIndex = 0;
        _applicationResults.Reset();
    }

    private void NormalizeActionSelection() => _applicationResults.Normalize(
        _selectedIndex,
        _selectedIndex >= 0 && _selectedIndex < _filteredApplications.Count
            ? _filteredApplications[_selectedIndex]
            : null);

    private void UpdateApplications()
    {
        var applications = _catalog.Snapshot;
        if (ReferenceEquals(applications, _applications))
        {
            return;
        }

        _applications = applications;
        ApplyFilter();
    }

    public void Dispose() => _catalog.Dispose();
}

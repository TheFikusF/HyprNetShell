using HyprNetShell.GUI;

using System.Globalization;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Platform;

using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class UnifiedSearchTab(
    IHyprctl hyprctl,
    UrlLauncher urlLauncher,
    Action closeDialog,
    ClipboardHistoryService clipboard,
    TextInputCoordinator inputs,
    Func<IReadOnlyList<UnifiedSearchTab.WindowResult>>? windows = null,
    Action<string>? focusWindow = null) : IMainDialogTab, IDisposable
{
    private const int FUZZY_SCORE_CUTOFF = 35;
    private const int MAXIMUM_APPLICATION_RESULTS = 8;

    private readonly DesktopApplicationCatalog _catalog = new();
    private readonly AppIconResolver _windowIcons = new();
    private readonly Dictionary<int, ModulesCommon.BoxState> _buttonStates = [];
    private readonly ApplicationResultInteraction _applicationResults = new();
    private readonly TextInputCoordinator.Input _queryInput = inputs.Create(
        "",
        "",
        "Search apps, type =1+2, or ?web search...",
        4096,
        alwaysActive: true);

    private IReadOnlyList<DesktopApplication> _applications = [];
    private IReadOnlyList<SearchResult> _results = [];
    private int _firstIndex;
    private int _selectedIndex;
    private bool _activating;
    private bool _disposed;
    private int _visibleItemCount = BoundedListUi.DEFAULT_VISIBLE_ITEM_COUNT;
    private IReadOnlyList<WindowResult> _windows = [];

    internal sealed record WindowResult(
            string Address,
            string Title,
            string Description,
            string ClassName = "",
            string InitialClassName = "");
    internal bool HasQuery => _queryInput.Value.Length > 0;
    internal void ClearQuery() => inputs.SetValue(_queryInput, "", notify: true);
    internal void Deactivate()
    {
        if (inputs.IsActive(_queryInput))
        {
            inputs.Deactivate();
        }
    }

    internal Node DrawOverview(int availableHeight)
    {
        UpdateApplications();
        var visibleItemCount = Math.Clamp((availableHeight - 90) / 74, 1, BoundedListUi.DEFAULT_VISIBLE_ITEM_COUNT);
        if (_visibleItemCount != visibleItemCount)
        {
            _visibleItemCount = visibleItemCount;
            BoundedListUi.Normalize(ref _selectedIndex, ref _firstIndex, _results.Count, _visibleItemCount);
        }
        return new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(ThemeManager.Current.Panel) with {
                Padding = 12,
                Spacing = 8,
                BorderRadius = 12,
                BorderWidth = ThemeManager.Current.Border.Width,
            },
            Children = HasQuery
                ? [inputs.Build(_queryInput), BoundedListUi.BuildList(_results, BuildRow, _firstIndex, _visibleItemCount,
                    delta => BoundedListUi.MoveViewport(ref _firstIndex, delta > 0 ? -1 : 1, _results.Count, _visibleItemCount))]
                : [inputs.Build(_queryInput)],
        };
    }

    public string Id => "unified-search";
    public string Title => "Search";
    public SvgAsset Icon => Icons.Search;

    public void Activate()
    {
        inputs.Configure(_queryInput, _ => RebuildResults());
        inputs.Activate(_queryInput);
        _catalog.RefreshSoon();
        UpdateApplications();
        RebuildResults();
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is SelectionDirection.Up or SelectionDirection.Down)
        {
            BoundedListUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1,
                _results.Count,
                _visibleItemCount);
            NormalizeActionSelection();
            return;
        }

        if (_activating ||
            _selectedIndex < 0 ||
            _selectedIndex >= _results.Count ||
            _results[_selectedIndex].Application is not { } application)
        {
            return;
        }

        _applicationResults.MoveHorizontal(_selectedIndex, application, direction);
    }

    public void ActivateSelection()
    {
        if (_activating || _selectedIndex < 0 || _selectedIndex >= _results.Count)
        {
            return;
        }

        var result = _results[_selectedIndex];
        switch (result.Kind)
        {
            case ResultKind.Application when result.Application is not null:
                var application = result.Application;
                var action = _applicationResults.SelectedAction(_selectedIndex, application);
                _activating = true;
                _ = LaunchApplicationAsync(application, action);
                break;
            case ResultKind.Window:
                focusWindow?.Invoke(result.Value);
                break;
            case ResultKind.Calculation:
                _ = clipboard.CopyTextAsync(result.Value);
                break;
            case ResultKind.BrowserSearch:
                OpenBrowserSearch(result.Value);
                break;
        }
    }

    public Node Draw()
    {
        UpdateApplications();
        return new BoxNode {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Search",
                    _queryInput.Value.Length == 0
                        ? "Apps, calculations, and the web"
                        : MainDialogTabUi.ResultCount(_selectedIndex, _results.Count, "No results")),
                inputs.Build(_queryInput),
                BoundedListUi.BuildScrollableResults(
                    new BoxNode
                    {
                        Direction = Direction.Vertical,
                        HorizontalAlignment = ItemsAlignment.Stretch,
                        Style = new Style { Spacing = 8 },
                        Children = _results
                            .VisibleItems(_firstIndex)
                            .Select(item => BuildRow(item.Item, item.Index))
                            .ToArray(),
                    },
                    _firstIndex,
                    _results.Count,
                    BoundedListUi.DEFAULT_VISIBLE_ITEM_COUNT),
            ],
        };
    }

    private Node BuildRow(SearchResult result, int index)
    {
        var selected = index == _selectedIndex;
        if (result.Application is { } application)
        {
            return _applicationResults.BuildRow(
                application,
                index,
                selected,
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
        }

        var state = _buttonStates.GetState(index, ThemeManager.Current.Panel).UpdateColor(selected ? ThemeManager.Current.Active : ThemeManager.Current.Panel);
        var fallbackIcon = result.Kind switch {
            ResultKind.Calculation => Icons.Calculator,
            ResultKind.BrowserSearch => Icons.Globe,
            _ => Icons.Application,
        };

        return new BoxNode(height: 66) {
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                _selectedIndex = index;
                ActivateSelection();
            },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(state.Background) with {
                BorderRadius = 8,
                BorderWidth = selected ? ThemeManager.Current.Border.Width : 0,
                Padding = new Insets(16, 10),
                Spacing = 14,
            },
            Children =
            [
                result.Window is { } window
                                    ? BuildWindowIcon(window, state.Background)
                                    : new ImageNode(fallbackIcon, 38, 38, ThemeManager.Current.Text),
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 4 },
                    Children =
                    [
                        new TextNode(result.Title, 18),
                        new TextNode(result.Description, color: selected ? ThemeManager.Current.Text : ThemeManager.Current.Text.MutedColor),
                    ],
                },
            ],
        };
    }

    private Node BuildWindowIcon(WindowResult window, Color background)
    {
        var application = _applications.FirstOrDefault(application =>
            MatchesApplication(application, window.ClassName) ||
            MatchesApplication(application, window.InitialClassName));
        var iconPath = string.IsNullOrWhiteSpace(application?.Icon)
            ? null
            : _windowIcons.TryResolveIcon(application.Icon);
        iconPath ??= _windowIcons.TryResolve(window.ClassName);
        iconPath ??= _windowIcons.TryResolve(window.InitialClassName);

        return new BoxNode(38, 38) {
            Children =
            [
                iconPath is not null
                    ? new ImageNode(iconPath, 38, 38)
                    : new ImageNode(Icons.Application, 38, 38, ThemeManager.Current.Text),
                new BoxNode(18, 18)
                {
                    IgnoreLayout = true,
                    Right = 0,
                    Bottom = 0,
                    HorizontalAlignment = ItemsAlignment.Center,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { BackgroundColor = background, BorderRadius = 4 },
                    Children = [new ImageNode(Icons.Application, 14, 14, ThemeManager.Current.Text)],
                },
            ],
        };
    }

    private static bool MatchesApplication(DesktopApplication application, string className) =>
        !string.IsNullOrWhiteSpace(className) &&
        (string.Equals(application.DesktopId, className, StringComparison.OrdinalIgnoreCase) ||
         string.Equals(application.Name, className, StringComparison.OrdinalIgnoreCase));

    private void UpdateApplications()
    {
        if (windows is not null)
        {
            var currentWindows = windows();
            if (!_windows.SequenceEqual(currentWindows))
            {
                _windows = currentWindows;
                RebuildResults();
            }
        }
        var applications = _catalog.Snapshot;
        if (ReferenceEquals(applications, _applications))
        {
            return;
        }

        _applications = applications;
        RebuildResults();
    }

    private void RebuildResults()
    {
        var query = _queryInput.Value.Trim();
        if (query.Length == 0)
        {
            _results = [];
            _firstIndex = 0;
            _selectedIndex = 0;
            _applicationResults.Reset();
            return;
        }

        var results = new List<SearchResult>();
        var calculatorOnly = query.StartsWith('=');
        var browserOnly = query.StartsWith('?');
        var interpretedQuery = calculatorOnly || browserOnly ? query[1..].Trim() : query;

        if (!browserOnly && LooksLikeCalculation(interpretedQuery) &&
            ExpressionEvaluator.TryEvaluate(interpretedQuery, out var calculation))
        {
            var value = calculation.ToString("G15", CultureInfo.InvariantCulture);
            results.Add(new SearchResult(ResultKind.Calculation, $"= {value}", "Press Enter to copy", value));
        }

        if (!calculatorOnly && !browserOnly)
        {
            results.AddRange(_windows
                .Select(window => (Window: window, Score: Math.Max(
                                    FuzzySearch.Score(interpretedQuery, window.Title),
                                    Math.Max(FuzzySearch.Score(interpretedQuery, window.ClassName),
                                        FuzzySearch.Score(interpretedQuery, window.InitialClassName)))))
                .Where(result => result.Score >= FUZZY_SCORE_CUTOFF)
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Window.Title, StringComparer.CurrentCultureIgnoreCase)
                .Take(MAXIMUM_APPLICATION_RESULTS)
                .Select(result => new SearchResult(ResultKind.Window, result.Window.Title,
                    result.Window.Description, result.Window.Address, Window: result.Window)));

            results.AddRange(_applications
                .Select(application => new
                {
                    Application = application,
                    Score = Math.Max(
                        FuzzySearch.Score(interpretedQuery, application.Name),
                        FuzzySearch.Score(interpretedQuery, application.Comment ?? "") - 12),
                })
                .Where(result => result.Score >= FUZZY_SCORE_CUTOFF)
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Application.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MAXIMUM_APPLICATION_RESULTS)
                .Select(result => new SearchResult(
                    ResultKind.Application,
                    result.Application.Name,
                    result.Application.Comment ?? "Launch application",
                    result.Application.DesktopFile,
                    result.Application)));
        }

        if (!calculatorOnly && interpretedQuery.Length > 0)
        {
            results.Add(new SearchResult(
                ResultKind.BrowserSearch,
                $"Search the web for “{interpretedQuery}”",
                browserOnly ? "Explicit web search" : "Open in the default browser",
                interpretedQuery));
        }

        _results = results;
        _firstIndex = 0;
        _selectedIndex = 0;
        _applicationResults.Reset();
    }

    private void NormalizeActionSelection() => _applicationResults.Normalize(
        _selectedIndex,
        _selectedIndex >= 0 && _selectedIndex < _results.Count
            ? _results[_selectedIndex].Application
            : null);

    private async Task LaunchApplicationAsync(DesktopApplication application, DesktopAction? action)
    {
        try
        {
            if (await DesktopApplicationLaunch.TryLaunchAsync(
                    hyprctl,
                    application,
                    action,
                    "UnifiedSearch") && !_disposed && inputs.IsActive(_queryInput))
            {
                ClearQuery();
                closeDialog();
            }
        }
        finally
        {
            _activating = false;
        }
    }

    private static bool LooksLikeCalculation(string query) =>
        query.Length > 0 &&
        query.Any(char.IsDigit) &&
        query.All(character => char.IsDigit(character) || char.IsWhiteSpace(character) ||
            character is '.' or ',' or '+' or '-' or '*' or '/' or '(' or ')');

    private void OpenBrowserSearch(string query) =>
        urlLauncher.TryOpen($"https://www.google.com/search?q={Uri.EscapeDataString(query)}");


    public void Dispose()
    {
        _disposed = true;
        Deactivate();
        inputs.Configure(_queryInput);
        _catalog.Dispose();
    }

    private enum ResultKind
    {
        Application,
        Window,
        Calculation,
        BrowserSearch,
    }

    private sealed record SearchResult(
        ResultKind Kind,
        string Title,
        string Description,
        string Value,
        DesktopApplication? Application = null,
        WindowResult? Window = null);

}

using System.Diagnostics;
using System.Globalization;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.Hyprland;

using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class UnifiedSearchTab(
    IHyprctl hyprctl,
    Action closeDialog,
    Theme theme) : IMainDialogTab, IDisposable
{
    private const int FuzzyScoreCutoff = 35;
    private const int MaximumApplicationResults = 8;

    private readonly DesktopApplicationCatalog _catalog = new();
    private readonly Dictionary<int, ModulesCommon.BoxState> _buttonStates = [];
    private readonly ApplicationResultInteraction _applicationResults = new(theme);
    private IReadOnlyList<DesktopApplication> _applications = [];
    private IReadOnlyList<SearchResult> _results = [];
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;
    private bool _activating;

    public string Id => "unified-search";
    public string Title => "Search";
    public SvgAsset Icon => Icons.Search;

    public void Activate()
    {
        _catalog.RefreshSoon();
        UpdateApplications();
        RebuildResults();
    }

    public void HandleTextInput(string text)
    {
        _query += text;
        RebuildResults();
    }

    public void HandleBackspace()
    {
        if (_query.Length == 0)
        {
            return;
        }

        _query = MainDialogTabUi.RemoveLastTextElement(_query);
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
                _results.Count);
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
            case ResultKind.Calculation:
                _ = Task.Run(() => CopyToClipboard(result.Value));
                break;
            case ResultKind.BrowserSearch:
                OpenBrowserSearch(result.Value);
                closeDialog();
                break;
        }
    }

    public Node Draw()
    {
        UpdateApplications();
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Search",
                    _query.Length == 0
                        ? "Apps, calculations, and the web"
                        : MainDialogTabUi.ResultCount(_selectedIndex, _results.Count, "No results")),
                MainDialogTabUi.BuildInput(_query, "Search apps, type =1+2, or ?web search..."),
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
                    BoundedListUi.DefaultVisibleItemCount,
                    theme),
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

        var state = _buttonStates.GetState(index, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
        var fallbackIcon = result.Kind switch
        {
            ResultKind.Calculation => Icons.Calculator,
            ResultKind.BrowserSearch => Icons.Globe,
            _ => Icons.Application,
        };

        return new BoxNode(height: 66)
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
                new ImageNode(fallbackIcon, 38, 38, theme.Text),
                new BoxNode
                {
                    Direction = Direction.Vertical,
                    VerticalAlignment = ItemsAlignment.Center,
                    Style = new Style { Spacing = 4 },
                    Children =
                    [
                        new TextNode(result.Title, 18, theme.Text),
                        new TextNode(result.Description, theme.TextSize, selected ? theme.Text : theme.Muted),
                    ],
                },
            ],
        };
    }

    private void UpdateApplications()
    {
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
        var query = _query.Trim();
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
            results.AddRange(_applications
                .Select(application => new
                {
                    Application = application,
                    Score = Math.Max(
                        FuzzySearch.Score(interpretedQuery, application.Name),
                        FuzzySearch.Score(interpretedQuery, application.Comment ?? "") - 12),
                })
                .Where(result => result.Score >= FuzzyScoreCutoff)
                .OrderByDescending(result => result.Score)
                .ThenBy(result => result.Application.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(MaximumApplicationResults)
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
                    "UnifiedSearch"))
            {
                _query = "";
                RebuildResults();
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

    private static void OpenBrowserSearch(string query)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"https://www.google.com/search?q={Uri.EscapeDataString(query)}",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Browser integration is optional; keep the shell responsive if no handler exists.
        }
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "wl-copy",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (process is null)
            {
                return;
            }

            process.StandardInput.Write(text);
            process.StandardInput.Close();
            process.WaitForExit(800);
        }
        catch
        {
            // Clipboard integration is optional; the calculated result remains visible.
        }
    }

    public void Dispose() => _catalog.Dispose();

    private enum ResultKind
    {
        Application,
        Calculation,
        BrowserSearch,
    }

    private sealed record SearchResult(
        ResultKind Kind,
        string Title,
        string Description,
        string Value,
        DesktopApplication? Application = null);

}

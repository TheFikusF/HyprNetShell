using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class DictionaryTab(DictionaryService dictionary, Theme theme) : IMainDialogTab, IDisposable
{
    private const int VisibleResultCount = 5;
    private const int MaximumQueryLength = 160;

    private readonly Lock _stateLock = new();
    private readonly Dictionary<int, ModulesCommon.BoxState> _resultStates = [];
    private DictionaryLookupResult _result = DictionaryLookupResult.Empty;
    private CancellationTokenSource? _lookupCancellation;
    private string _query = "";
    private int _selectedIndex;
    private int _firstIndex;
    private bool _isLookingUp;
    private bool _disposed;

    public string Id => "dictionary";
    public string Title => "Dictionary";
    public SvgAsset Icon => Icons.Dictionary;

    public void Activate()
    {
        lock (_stateLock)
        {
            _selectedIndex = 0;
            _firstIndex = 0;
        }
    }

    public void HandleTextInput(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_stateLock)
        {
            var available = MaximumQueryLength - _query.Length;
            if (available <= 0)
            {
                return;
            }

            _query += text[..Math.Min(text.Length, available)];
            InvalidateLookup();
        }
    }

    public void HandleBackspace()
    {
        lock (_stateLock)
        {
            if (_query.Length == 0)
            {
                return;
            }

            _query = MainDialogTabUi.RemoveLastTextElement(_query);
            InvalidateLookup();
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        if (direction is not (SelectionDirection.Up or SelectionDirection.Down))
        {
            return;
        }

        lock (_stateLock)
        {
            BoundedListUi.MoveSelection(
                ref _selectedIndex,
                ref _firstIndex,
                direction == SelectionDirection.Up ? -1 : 1,
                _result.Items.Count,
                VisibleResultCount);
        }
    }

    public void ActivateSelection()
    {
        CancellationTokenSource cancellation;
        string query;
        lock (_stateLock)
        {
            if (_disposed || _isLookingUp || string.IsNullOrWhiteSpace(_query))
            {
                return;
            }

            _lookupCancellation?.Cancel();
            _lookupCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _lookupCancellation = cancellation;
            _isLookingUp = true;
            query = _query;
        }

        _ = LookupAsync(query, cancellation);
    }

    public Node Draw()
    {
        string query;
        DictionaryLookupResult result;
        int selectedIndex;
        int firstIndex;
        bool isLookingUp;
        lock (_stateLock)
        {
            query = _query;
            result = _result;
            selectedIndex = _selectedIndex;
            firstIndex = _firstIndex;
            isLookingUp = _isLookingUp;
        }

        var status = BuildStatus(result, isLookingUp);
        var content = new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children = result.Items.Count == 0
                ? [new TextNode(EmptyMessage(query, result, isLookingUp), 18, theme.Text.MutedColor)]
                : result.Items
                    .VisibleItems(firstIndex, VisibleResultCount)
                    .Select(item => BuildResult(item.Item, item.Index, selectedIndex))
                    .ToArray(),
        };

        var children = new List<Node>
        {
            MainDialogTabUi.BuildSectionHeader("Dictionary", status),
            MainDialogTabUi.BuildInput(query, "Type an English word or phrase, then press Enter..."),
            BoundedListUi.BuildScrollableResults(
                content,
                firstIndex,
                result.Items.Count,
                VisibleResultCount,
                theme),
        };
        if (result.Errors.Count > 0)
        {
            children.Add(new TextNode(string.Join(" · ", result.Errors), theme.Text, theme.Warning));
        }

        return new BoxNode(new Style { Spacing = 8 })
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children = children,
        };
    }

    private async Task LookupAsync(string query, CancellationTokenSource cancellation)
    {
        try
        {
            var result = await dictionary.LookupAsync(query, cancellation.Token);
            lock (_stateLock)
            {
                if (_lookupCancellation != cancellation)
                {
                    return;
                }

                _result = result;
                _selectedIndex = 0;
                _firstIndex = 0;
                _resultStates.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Dictionary", "Dictionary lookup failed", exception);
            lock (_stateLock)
            {
                if (_lookupCancellation == cancellation)
                {
                    _result = new DictionaryLookupResult(query, [], ["Lookup failed"]);
                }
            }
        }
        finally
        {
            lock (_stateLock)
            {
                if (_lookupCancellation == cancellation)
                {
                    _lookupCancellation = null;
                    _isLookingUp = false;
                }
            }

            cancellation.Dispose();
        }
    }

    private BoxNode BuildResult(DictionaryResultItem item, int index, int selectedIndex)
    {
        var selected = index == selectedIndex;
        var state = _resultStates.GetState(index, theme.Panel).UpdateColor(selected ? theme.Active : theme.Panel);
        var details = item.Example is { Length: > 0 }
            ? $"Example: {Truncate(item.Example, 100)}"
            : item.Attribution;

        return new BoxNode(height: 90)
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () => Select(index),
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.Border.Width : 0,
                Padding = new Insets(14, 8),
                Spacing = 5,
            },
            Children =
            [
                new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
                {
                    new TextNode(Truncate(item.Heading, 70), 17, theme.Text),
                    new TextNode(item.Source, theme.Text, theme.Text.MutedColor),
                },
                new TextNode(Truncate(item.Definition, 145), theme.Text, theme.Text),
                new TextNode(Truncate(details, 120), theme.Text, theme.Text.MutedColor),
            ],
        };
    }

    private void Select(int index)
    {
        lock (_stateLock)
        {
            _selectedIndex = index;
            BoundedListUi.Normalize(
                ref _selectedIndex,
                ref _firstIndex,
                _result.Items.Count,
                VisibleResultCount);
        }
    }

    private void InvalidateLookup()
    {
        _lookupCancellation?.Cancel();
        _lookupCancellation = null;
        _isLookingUp = false;
        _result = DictionaryLookupResult.Empty;
        _selectedIndex = 0;
        _firstIndex = 0;
        _resultStates.Clear();
    }

    private static string BuildStatus(DictionaryLookupResult result, bool isLookingUp)
    {
        if (isLookingUp)
        {
            return "Looking up all providers…";
        }

        if (result.Query.Length == 0)
        {
            return "Dictionary · Urban Dictionary · translation";
        }

        return result.Items.Count == 0
            ? "No results"
            : $"{result.Items.Count} results · ↑/↓ to browse";
    }

    private static string EmptyMessage(
        string query,
        DictionaryLookupResult result,
        bool isLookingUp) =>
        isLookingUp
            ? "Requesting definitions, slang, and translation…"
            : query.Length == 0
                ? "Enter searches all providers. Translation language is configurable with HYPRNETSHELL_TRANSLATION_LANGUAGE."
                : result.Query.Length == 0
                    ? "Press Enter to search."
                    : "No provider returned a result.";

    private static string Truncate(string? value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumLength ? normalized : normalized[..(maximumLength - 1)] + "…";
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _lookupCancellation?.Cancel();
            _lookupCancellation = null;
            _isLookingUp = false;
        }
    }
}

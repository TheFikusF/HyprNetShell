using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Platform;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class DictionaryTab(DictionaryService dictionary, Theme theme) : IMainDialogTab, IDisposable
{
    private sealed class ResultState : ModulesCommon.BoxState
    {
        public ModulesCommon.BoxState Copy { get; } = new();
    }

    private const int VisibleResultCount = 5;
    private const int MaximumQueryLength = 160;
    private const int SearchSelectionIndex = -1;

    private readonly Lock _stateLock = new();
    private readonly Dictionary<int, ResultState> _resultStates = [];
    private readonly ModulesCommon.BoxState _searchState = new();
    private DictionaryLookupResult _result = DictionaryLookupResult.Empty;
    private CancellationTokenSource? _lookupCancellation;
    private string _query = "";
    private int _selectedIndex = SearchSelectionIndex;
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
            _selectedIndex = SearchSelectionIndex;
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
            var itemCount = _result.Items.Count;
            if (itemCount == 0)
            {
                _selectedIndex = SearchSelectionIndex;
                return;
            }

            if (direction == SelectionDirection.Up)
            {
                _selectedIndex = _selectedIndex switch
                {
                    SearchSelectionIndex => itemCount - 1,
                    0 => SearchSelectionIndex,
                    _ => _selectedIndex - 1,
                };
            }
            else
            {
                _selectedIndex = _selectedIndex switch
                {
                    SearchSelectionIndex => 0,
                    _ when _selectedIndex == itemCount - 1 => SearchSelectionIndex,
                    _ => _selectedIndex + 1,
                };
            }

            if (_selectedIndex >= 0)
            {
                BoundedListUi.Normalize(
                    ref _selectedIndex,
                    ref _firstIndex,
                    itemCount,
                    VisibleResultCount);
            }
        }
    }

    public void ActivateSelection()
    {
        CancellationTokenSource? cancellation = null;
        string? query = null;
        string? textToCopy = null;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_selectedIndex >= 0 && _selectedIndex < _result.Items.Count)
            {
                textToCopy = GetTextToCopy(_result.Items[_selectedIndex]);
            }
            else if (!_isLookingUp && !string.IsNullOrWhiteSpace(_query))
            {
                _lookupCancellation?.Cancel();
                _lookupCancellation?.Dispose();
                cancellation = new CancellationTokenSource();
                _lookupCancellation = cancellation;
                _isLookingUp = true;
                query = _query;
            }
        }

        if (textToCopy is not null)
        {
            Utils.CopyToClipboard(textToCopy);
        }
        else if (cancellation is not null)
        {
            _ = LookupAsync(query!, cancellation);
        }
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
            Style = Style.Spacer,
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
            new BoxNode(height: 46)
            {
                HorizontalAlignment = ItemsAlignment.Stretch,
                VerticalAlignment = ItemsAlignment.Center,
                Style = Style.Spacer,
                Children =
                [
                    MainDialogTabUi.BuildInput(query, "Type an English word or phrase..."),
                    BuildSearchButton(selectedIndex == SearchSelectionIndex, isLookingUp),
                ],
            },
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

        return new BoxNode(Style.Spacer)
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
                _selectedIndex = result.Items.Count == 0 ? SearchSelectionIndex : 0;
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
            ? $"Example: {item.Example}"
            : item.Attribution ?? "";

        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                if (!state.Copy.Hovered.Value)
                {
                    Select(index);
                }
            },
            IsHovered = state.Hovered,
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.Border.Width : 0,
                Padding = new Insets(8, 8, 8, 16),
                Spacing = 4,
            },
            Children =
            [
                new BoxNode(Style.Spacer, ItemsAlignment.Spread, ItemsAlignment.Center)
                {
                    new TextNode(item.Heading, theme.Text.HeaderSize, theme.Text,
                        maxWidth: 600, wrapping: TextWrapping.Wrap),
                    new BoxNode(Style.Spacer, verticalAlignment: ItemsAlignment.Center)
                    {
                        new TextNode(item.Source, theme.Text, theme.Text.MutedColor),
                        BuildCopyButton(item, state.Copy),
                    },
                },
                new TextNode(item.Definition, theme.Text, theme.Text,
                    maxWidth: 820, wrapping: TextWrapping.Wrap),
                new TextNode(details, theme.Text, theme.Text.MutedColor,
                    maxWidth: 820, wrapping: TextWrapping.Wrap),
            ],
        };
    }

    private BoxNode BuildSearchButton(bool selected, bool isLookingUp)
    {
        _searchState.UpdateColor(selected ? theme.Active : theme.Panel);
        return new BoxNode(46, 46)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = _searchState.Hovered,
            OnClick = isLookingUp ? null : SelectSearchAndActivate,
            Style = ModulesCommon.ModuleStyle(theme, _searchState.Background) with
            {
                BorderRadius = 8,
                BorderWidth = selected ? theme.Border.Width : 0,
                Padding = 0,
            },
            Children = [new ImageNode(Icons.Search, 18, 18, isLookingUp ? theme.Text.MutedColor : theme.Text)],
        };
    }

    private BoxNode BuildCopyButton(DictionaryResultItem item, ModulesCommon.BoxState state)
    {
        var details = item.Example is { Length: > 0 }
            ? $"\nExample: {item.Example}"
            : "";
        state.UpdateColor(theme.Panel);
        return new BoxNode(32, 32)
        {
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            IsHovered = state.Hovered,
            OnClick = () => Utils.CopyToClipboard(GetTextToCopy(item)),
            Style = ModulesCommon.ModuleStyle(theme, state.Background) with
            {
                BorderRadius = 8,
                BorderWidth = 0,
                Padding = 0,
                ShadowColor = null,
            },
            Children = [new ImageNode(Icons.Copy, 16, 16, theme.Text)],
        };
    }

    private static string GetTextToCopy(DictionaryResultItem item)
    {
        var details = item.Example is { Length: > 0 }
            ? $"\nExample: {item.Example}"
            : "";

        return $"{item.Heading}\nDefinition: {item.Definition}{details}\nSource: {item.Source}";
    }

    private void SelectSearchAndActivate()
    {
        lock (_stateLock)
        {
            _selectedIndex = SearchSelectionIndex;
        }

        ActivateSelection();
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
        _selectedIndex = SearchSelectionIndex;
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

using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class WallpapersTab(IHyprctl hyprctl, Action closeDialog) : IMainDialogTab
{
    private const int COLUMNS = 4;
    private const int ROWS = 4;
    private const int VISIBLE_WALLPAPER_COUNT = COLUMNS * ROWS;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".jpeg", ".jpg", ".jxl", ".png", ".tif", ".tiff", ".webp",
    };

    private readonly Lock _stateLock = new();
    private IReadOnlyList<Wallpaper> _wallpapers = [];
    private IReadOnlyList<Wallpaper> _filteredWallpapers = [];
    private CancellationTokenSource? _loadCancellation;
    private string _query = "";
    private int _firstIndex;
    private int _selectedIndex;
    private bool _isLoading;

    public string Title => "Wallpapers";
    public SvgAsset Icon => Icons.Wallpaper;

    public void Activate()
    {
        CancellationTokenSource cancellation;
        lock (_stateLock)
        {
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            cancellation = new CancellationTokenSource();
            _loadCancellation = cancellation;
            _isLoading = true;
            _firstIndex = 0;
            _selectedIndex = 0;
        }

        _ = LoadWallpapersAsync(cancellation);
    }

    public void HandleTextInput(string text)
    {
        lock (_stateLock)
        {
            _query += text;
            ApplyFilterLocked();
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
            ApplyFilterLocked();
        }
    }

    public void MoveSelection(SelectionDirection direction)
    {
        lock (_stateLock)
        {
            if (_filteredWallpapers.Count == 0)
            {
                return;
            }

            _selectedIndex = direction switch
            {
                SelectionDirection.Up => MoveVertical(-1),
                SelectionDirection.Down => MoveVertical(1),
                SelectionDirection.Left => MoveHorizontal(-1),
                SelectionDirection.Right => MoveHorizontal(1),
                _ => _selectedIndex,
            };
            AlignViewportToSelectionLocked();
        }
    }

    private int MoveHorizontal(int direction)
    {
        var rowStart = _selectedIndex / COLUMNS * COLUMNS;
        var rowLength = Math.Min(COLUMNS, _filteredWallpapers.Count - rowStart);
        var column = _selectedIndex - rowStart;
        return rowStart + PositiveModulo(column + direction, rowLength);
    }

    private int MoveVertical(int direction)
    {
        var column = _selectedIndex % COLUMNS;
        var row = _selectedIndex / COLUMNS;
        var rowsInColumn = (_filteredWallpapers.Count - 1 - column) / COLUMNS + 1;
        return PositiveModulo(row + direction, rowsInColumn) * COLUMNS + column;
    }

    private void AlignViewportToSelectionLocked()
    {
        var selectedRow = _selectedIndex / COLUMNS;
        var firstVisibleRow = _firstIndex / COLUMNS;
        if (selectedRow < firstVisibleRow)
        {
            firstVisibleRow = selectedRow;
        }
        else if (selectedRow >= firstVisibleRow + ROWS)
        {
            firstVisibleRow = selectedRow - ROWS + 1;
        }

        var totalRows = (_filteredWallpapers.Count + COLUMNS - 1) / COLUMNS;
        var maximumFirstRow = Math.Max(0, totalRows - ROWS);
        _firstIndex = Math.Min(firstVisibleRow, maximumFirstRow) * COLUMNS;
    }

    public void ActivateSelection()
    {
        string path;
        lock (_stateLock)
        {
            if (_selectedIndex < 0 || _selectedIndex >= _filteredWallpapers.Count)
            {
                return;
            }

            path = _filteredWallpapers[_selectedIndex].Path;
        }

        _ = hyprctl.SetWallpaperAsync(path);
        closeDialog();
    }

    public Node Draw()
    {
        Wallpaper[] visible;
        string query;
        string status;
        int firstIndex;
        int selectedIndex;
        int totalCount;
        lock (_stateLock)
        {
            visible = _filteredWallpapers
                .Skip(_firstIndex)
                .Take(VISIBLE_WALLPAPER_COUNT)
                .ToArray();
            query = _query;
            firstIndex = _firstIndex;
            selectedIndex = _selectedIndex;
            totalCount = _filteredWallpapers.Count;
            status = _isLoading
                ? "Loading wallpapers..."
                : MainDialogTabUi.ResultCount(
                    _selectedIndex,
                    _filteredWallpapers.Count,
                    _query.Length == 0 ? "No wallpapers in ~/Pictures/wp" : "No matching wallpapers");
        }

        var grid = new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                ..Enumerable.Range(0, ROWS).Select(row => BuildRow(visible, row, firstIndex, selectedIndex)),
            ],
        };
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = new Style { Spacing = 8 },
            Children =
            [
                MainDialogTabUi.BuildSectionHeader(
                    "Wallpapers",
                    status),
                MainDialogTabUi.BuildInput(query, "Search wallpapers..."),
                MainDialogTabUi.BuildScrollableResults(
                    grid,
                    firstIndex / COLUMNS,
                    (totalCount + COLUMNS - 1) / COLUMNS,
                    ROWS),
            ],
        };
    }

    private Node BuildRow(IReadOnlyList<Wallpaper> visible, int row, int firstIndex, int selectedIndex)
    {
        var tiles = new Node[COLUMNS];
        for (var column = 0; column < COLUMNS; column++)
        {
            var visibleIndex = row * COLUMNS + column;
            tiles[column] = visibleIndex < visible.Count
                ? BuildTile(visible[visibleIndex], firstIndex + visibleIndex, selectedIndex)
                : new BoxNode();
        }

        return new BoxNode
        {
            HorizontalAlignment = ItemsAlignment.Start,
            VerticalAlignment = ItemsAlignment.Center,
            Style = new Style { Spacing = 8 },
            Children = tiles,
        };
    }

    private Node BuildTile(Wallpaper wallpaper, int index, int selectedIndex)
    {
        var selected = index == selectedIndex;
        return new BoxNode
        {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Center,
            VerticalAlignment = ItemsAlignment.Center,
            OnClick = () =>
            {
                lock (_stateLock)
                {
                    _selectedIndex = index;
                }
                ActivateSelection();
            },
            Style = ModulesCommon.ModuleStyle(
                Theme.Default,
                selected ? Theme.Default.Active : Theme.Default.Panel) with
            {
                Padding = 4 + (selected ? 0 : Theme.Default.BorderWidth),
                Spacing = 4,
                BorderRadius = 6,
                BorderWidth = selected ? Theme.Default.BorderWidth : 0,
            },
            Children =
            [
                new ImageNode(wallpaper.Path, (int)(192 * 0.98f), (int)(108 * 0.98f), loadAsync: true),
                new TextNode(MainDialogTabUi.Trim(wallpaper.Name, 24), 14.0f, Theme.Default.Text),
            ],
        };
    }

    private async Task LoadWallpapersAsync(CancellationTokenSource cancellation)
    {
        IReadOnlyList<Wallpaper> wallpapers;
        try
        {
            wallpapers = await Task.Run(() => LoadWallpapers(cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_stateLock)
        {
            if (!ReferenceEquals(_loadCancellation, cancellation))
            {
                return;
            }

            _wallpapers = wallpapers;
            _isLoading = false;
            ApplyFilterLocked();
        }
    }

    private static IReadOnlyList<Wallpaper> LoadWallpapers(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Pictures",
            "wp");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var wallpapers = new List<Wallpaper>();
            foreach (var path in Directory.EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                }))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    wallpapers.Add(new Wallpaper(Path.GetFullPath(path), Path.GetFileName(path)));
                }
            }

            return wallpapers
                .OrderBy(wallpaper => wallpaper.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
        }
    }

    private void ApplyFilterLocked()
    {
        _filteredWallpapers = string.IsNullOrWhiteSpace(_query)
            ? _wallpapers
            : _wallpapers
                .Where(wallpaper => wallpaper.Name.Contains(_query, StringComparison.CurrentCultureIgnoreCase))
                .ToArray();
        _firstIndex = 0;
        _selectedIndex = 0;
    }

    private static int PositiveModulo(int value, int divisor) => (value % divisor + divisor) % divisor;

    private sealed record Wallpaper(string Path, string Name);
}

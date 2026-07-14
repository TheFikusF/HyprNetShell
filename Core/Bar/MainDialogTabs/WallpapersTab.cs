using System.Diagnostics;
using HyprNetShell.Core.Assets;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class WallpapersTab(Action closeDialog) : IMainDialogTab
{
    private const int Columns = 4;
    private const int Rows = 4;
    private const int VisibleWallpaperCount = Columns * Rows;

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".heic", ".jpeg", ".jpg", ".jxl", ".png", ".tif", ".tiff", ".webp",
    };

    private readonly object _stateLock = new();
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
        var rowStart = _selectedIndex / Columns * Columns;
        var rowLength = Math.Min(Columns, _filteredWallpapers.Count - rowStart);
        var column = _selectedIndex - rowStart;
        return rowStart + PositiveModulo(column + direction, rowLength);
    }

    private int MoveVertical(int direction)
    {
        var column = _selectedIndex % Columns;
        var row = _selectedIndex / Columns;
        var rowsInColumn = (_filteredWallpapers.Count - 1 - column) / Columns + 1;
        return PositiveModulo(row + direction, rowsInColumn) * Columns + column;
    }

    private void AlignViewportToSelectionLocked()
    {
        var selectedRow = _selectedIndex / Columns;
        var firstVisibleRow = _firstIndex / Columns;
        if (selectedRow < firstVisibleRow)
        {
            firstVisibleRow = selectedRow;
        }
        else if (selectedRow >= firstVisibleRow + Rows)
        {
            firstVisibleRow = selectedRow - Rows + 1;
        }

        var totalRows = (_filteredWallpapers.Count + Columns - 1) / Columns;
        var maximumFirstRow = Math.Max(0, totalRows - Rows);
        _firstIndex = Math.Min(firstVisibleRow, maximumFirstRow) * Columns;
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

        _ = Task.Run(() => SetWallpaperAsync(path));
        closeDialog();
    }

    public Node Draw()
    {
        Wallpaper[] visible;
        string query;
        string status;
        int firstIndex;
        int selectedIndex;
        lock (_stateLock)
        {
            visible = _filteredWallpapers
                .Skip(_firstIndex)
                .Take(VisibleWallpaperCount)
                .ToArray();
            query = _query;
            firstIndex = _firstIndex;
            selectedIndex = _selectedIndex;
            status = _isLoading
                ? "Loading wallpapers..."
                : MainDialogTabUi.ResultCount(
                    _selectedIndex,
                    _filteredWallpapers.Count,
                    _query.Length == 0 ? "No wallpapers in ~/Pictures/wp" : "No matching wallpapers");
        }

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
                ..Enumerable.Range(0, Rows).Select(row => BuildRow(visible, row, firstIndex, selectedIndex)),
            ],
        };
    }

    private Node BuildRow(IReadOnlyList<Wallpaper> visible, int row, int firstIndex, int selectedIndex)
    {
        var tiles = new Node[Columns];
        for (var column = 0; column < Columns; column++)
        {
            var visibleIndex = row * Columns + column;
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
        return new BoxNode()
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
                new ImageNode(wallpaper.Path, 192, 108, loadAsync: true),
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

    private static async Task SetWallpaperAsync(string path)
    {
        if (await RunHyprpaperAsync("wallpaper", $", {path}, cover"))
        {
            return;
        }

        await RunHyprpaperAsync("reload", $",{path}");
    }

    private static async Task<bool> RunHyprpaperAsync(string dispatcher, string argument)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "hyprctl",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "hyprpaper", dispatcher, argument },
            });
            if (process is null)
            {
                return false;
            }

            await Task.WhenAll(
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync(),
                process.WaitForExitAsync());
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private sealed record Wallpaper(string Path, string Name);
}

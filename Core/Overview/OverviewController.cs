// #define DEBUG_OVERVIEW_ZOOM

using HyprNetShell.Core.Configuration;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.System;
using System.Diagnostics;
using HyprNetShell.Rendering;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;
using HyprNetShell.GUI;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Helpers;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Overview;

public sealed class OverviewController : IDisposable
{
    private readonly record struct WindowIdentity(string Address, string Title, int WorkspaceId, string MonitorName, int Width, int Height, string ClassName, string InitialClassName);
    public const float THUMBNAILS_SCALE = 1f;
    private const int OVERVIEW_PADDING = 24;
#if DEBUG_OVERVIEW_ZOOM
    private const int ZOOM_TEST_WIDTH = 220;
#endif
    private const double TRANSITION_DURATION = 0.3;

    private readonly HyprlandService _hyprland;
    private readonly IHyprctl _hyprctl;
    private readonly IWindowThumbnailService _thumbnails;
    private readonly OverviewBackgroundService _background;
    private readonly Ref<bool> _searchHovered = new();
    private readonly TextInputCoordinator _searchInputs;
    private readonly UnifiedSearchTab _search;
    private readonly UrlLauncher _searchUrls;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _bindingTask;
    private readonly Dictionary<string, float> _aspects = [];
#if DEBUG_OVERVIEW_ZOOM
    private readonly Ref<bool> _zoomTestDragging = new();
    private readonly Ref<bool> _zoomTestHovered = new();
#endif
    private readonly List<OverviewPreview> _previews = [];
    private readonly AppIconResolver _icons = new();
    private readonly Dictionary<string, int> _thumbnailIndices = new(StringComparer.Ordinal);

    private readonly IOverviewDrawer _crystalDrawer = new CrystalOverviewDrawer();
    private readonly IOverviewDrawer _classicDrawer = new ClassicOverviewDrawer();
    private IOverviewDrawer _drawer;

    private int _toggleRequests;
    private bool _open;
    private double _transitionStart;
    private float _transitionFrom;
    private string? _selected;
#if DEBUG_OVERVIEW_ZOOM
    private float? _zoomTest;
#endif

    private IReadOnlyList<WorkspaceSnapshot>? _workspaceSnapshot;
    private WindowIdentity[] _windows = [];
    private IReadOnlyList<WindowThumbnailSnapshot>? _thumbnailSnapshot;
    private HashSet<ulong> _nextDemand = [];
    private int _layoutWidth = -1;
    private int _layoutHeight = -1;
    private bool _layoutDirty = true;
    private double _lastDraw;

    private string _outputName = "";
    private HashSet<ulong> _demand = [];
    private bool _disposed;

    private int _openedWorkspaceId;

    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public ulong? OwnerOutputId
    {
        get; private set;
    }

    public bool IsVisible => _open;
    public ulong? RenderOutputId
    {
        get
        {
            if (!_open && Now - _transitionStart >= TRANSITION_DURATION)
            {
                OwnerOutputId = null;
                _previews.Clear();
                _drawer.Reset();
                _thumbnailSnapshot = null;
                _thumbnailIndices.Clear();
            }

            return OwnerOutputId;
        }
    }

    private float Progress
    {
        get
        {
            var t = (float)Math.Clamp((Now - _transitionStart) / TRANSITION_DURATION, 0, 1);
            t = 1 - MathF.Pow(1 - t, 3);
            return _transitionFrom + ((_open ? 1 : 0) - _transitionFrom) * t;
        }
    }

    internal OverviewController(HyprlandService hyprland, IHyprctl hyprctl, IWindowThumbnailService thumbnails,
        Func<string?> currentWallpaper, ClipboardHistoryService clipboard)
    {
        _drawer = _crystalDrawer;
        _hyprland = hyprland;
        _hyprctl = hyprctl;
        _thumbnails = thumbnails;
        _background = new OverviewBackgroundService(currentWallpaper);
        _searchInputs = new TextInputCoordinator(clipboard);
        _searchUrls = new UrlLauncher(hyprland, hyprctl, Close);
        _search = new UnifiedSearchTab(hyprctl, _searchUrls, Close, clipboard, _searchInputs,
            () => _hyprland.Snapshot.Workspaces
                .SelectMany(workspace => workspace.Windows.Select(window => new UnifiedSearchTab.WindowResult(
                    window.Address, window.Title, $"{workspace.MonitorName} · Workspace {workspace.Id}",
                                        window.ClassName, window.InitialClassName)))
                .Where(window => !string.IsNullOrEmpty(window.Address))
                .DistinctBy(window => window.Address).ToArray(), Activate);
        _bindingTask = Task.Run(() => InstallBindingAsync(_lifetime.Token));
    }

    public void RequestToggle() => Interlocked.Increment(ref _toggleRequests);

    public void ProcessPendingRequests(ulong? outputId, string? outputName)
    {
        var snapshot = _hyprland.Snapshot;
        if (_open && _openedWorkspaceId != snapshot.FocusedWorkspaceId)
        {
            Close();
        }

        if ((Interlocked.Exchange(ref _toggleRequests, 0) & 1) == 0)
        {
            return;
        }

        if (IsVisible)
        {
            Close();
        }

        else if (outputId.HasValue && outputName is not null)
        {
            _transitionFrom = OwnerOutputId == outputId ? Progress : 0;
            OwnerOutputId = outputId;
            _outputName = outputName;
            _transitionStart = Now;
            _open = true;

            _openedWorkspaceId = snapshot.FocusedWorkspaceId;

#if DEBUG_OVERVIEW_ZOOM
            _zoomTest = null;
            _zoomTestDragging.Value = false;
#endif
            _search.ClearQuery();
            _search.Activate();
            _selected = null;
            _aspects.Clear();
            _drawer.Reset();
            _workspaceSnapshot = null;
            _layoutDirty = true;
            _lastDraw = Now;
        }
    }

    public void HandleInput(int pressedKey, string textInput, bool controlPressed)
    {
        if (!_open)
        {
            return;
        }

        var key = DialogService.ToDialogKey(pressedKey, textInput);
        if (key == DialogKey.Escape)
        {
            if (_search.HasQuery)
            {
                _search.ClearQuery();
            }

            else
            {
                Close();
            }

            return;
        }

        if (controlPressed && key == DialogKey.PhysicalV)
        {
            _searchInputs.HandleKey(DialogKey.PhysicalV);
            return;
        }

        if (key == DialogKey.Backspace)
        {
            _searchInputs.HandleBackspace(controlPressed);
            return;
        }

        if (!controlPressed && textInput.Length > 0)
        {
            _searchInputs.HandleTextInput(textInput);
        }

        if (_search.HasQuery)
        {
            if (key == DialogKey.Enter)
            {
                _search.ActivateSelection();
            }

            else if (key is DialogKey.Up or DialogKey.Down or DialogKey.Left or DialogKey.Right)
            {
                _search.MoveSelection(key switch {
                    DialogKey.Up => SelectionDirection.Up,
                    DialogKey.Down => SelectionDirection.Down,
                    DialogKey.Left => SelectionDirection.Left,
                    _ => SelectionDirection.Right,
                });
            }

            return;
        }

        var current = _previews.FindIndex(p => p.Address == _selected);
        if (current < 0 && _previews.Count > 0)
        {
            current = 0;
        }

        if (key == DialogKey.Enter && current >= 0)
        {
            Activate(_previews[current].Address);
            return;
        }

        if (current >= 0 && _previews[current].Neighbors.TryGetValue(key, out var neighbor))
        {
            _selected = neighbor;
        }
    }

    private void Activate(string address)
    {
        _selected = address;
        _ = FocusAsync(address);
        Close();
    }

    public void Close()
    {
        if (_open)
        {
            _transitionFrom = Progress;
            _transitionStart = Now;
            _open = false;
        }

#if DEBUG_OVERVIEW_ZOOM
        _zoomTest = null;
        _zoomTestDragging.Value = false;
#endif
        _search.Deactivate();
        _demand.Clear();
        _thumbnails.RemoveOwner(this);
    }

    public void Draw(IRenderApi renderer, int width, int height)
    {
        var drawer = AppConfigurationStore.Shared.Snapshot.Visuals?.CrystalOverview != false ? _crystalDrawer : _classicDrawer;
        if (!ReferenceEquals(drawer, _drawer))
        {
            _drawer.Reset();
            _drawer = drawer;
            _drawer.Reset();
            _layoutDirty = true;
        }

        if (_open)
        {
            BuildPreviews(width, height);
        }

        var transitionProgress = Progress;
        var progress = transitionProgress;
        var previewTransition = !_open;
#if DEBUG_OVERVIEW_ZOOM
        transitionProgress = _open && _zoomTest.HasValue ? 1 - _zoomTest.Value : transitionProgress;
        progress = _open && _zoomTest.HasValue ? 1 : transitionProgress;
        previewTransition |= _zoomTest.HasValue;
#endif
        var now = Now;
        var dt = Math.Max(0, now - _lastDraw);
        _lastDraw = now;
        var theme = ThemeManager.Current;
#if DEBUG_OVERVIEW_ZOOM
        var zoomPanel = new BoxNode(Math.Min(ZOOM_TEST_WIDTH, Math.Max(80, width / 3))) {
            IsHovered = _zoomTestHovered,
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Style = ModulesCommon.ModuleStyle(theme.Panel) with { BorderRadius = 8, Spacing = 8 },
            Children =
            [
                new BoxNode {
                    Direction = Direction.Horizontal,
                    HorizontalAlignment = ItemsAlignment.Spread,
                    Children =
                    [
                        new TextNode("Zoom test"),
                        new BoxNode { OnClick = () => _zoomTest = null, Children = [new TextNode("Reset")] },
                    ],
                },
                new SliderNode(null, 20, _zoomTest ?? 0, theme.Text.MutedColor, Color.Orange,
                    theme.Text.Color, value => _zoomTest = value, _zoomTestDragging),
            ],
        };
#endif
        var localTime = DateTime.Now;
        var clock = new BoxNode() {
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.End,
            Style = Style.Spacer,
            Children =
            [
                new TextNode(localTime.ToString("HH:mm"), 64, theme.Text.Color),
                new TextNode(localTime.ToString("dddd, d MMMM"), 20, theme.Text.Color),
            ],
        };
        var sidePanel = new BoxNode {
            IgnoreLayout = true,
            Top = OVERVIEW_PADDING,
            Right = OVERVIEW_PADDING,
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.End,
            Style = Style.Spacer,
            Children = [clock],
        };
#if DEBUG_OVERVIEW_ZOOM
        if (_open)
        {
            sidePanel.Children.Add(zoomPanel);
        }
#endif
        var searchWidth = Math.Max(1, Math.Min(720, width - 2 * OVERVIEW_PADDING - 2 * (clock.Width + 16)));
        var search = new BoxNode(searchWidth) {
            IsHovered = _searchHovered,
            Direction = Direction.Vertical,
            HorizontalAlignment = ItemsAlignment.Stretch,
            Children = [_search.DrawOverview(height - 2 * OVERVIEW_PADDING)],
        };

        var hint = _previews.Count == 0 ? "No windows · Type to search"
            : "Type to search · Arrow keys to select · Enter to focus · Esc to clear / close";
        var overlay = new BoxNode(width, height) {
            OnClick = _open ? () => { } : null,
            Children =
            [
                new BoxNode(width) {
                    IgnoreLayout = true,
                    Top = OVERVIEW_PADDING,
                    HorizontalAlignment = ItemsAlignment.Center,
                    Children = [search],
                },
                sidePanel,
                new BoxNode(width - 2 * OVERVIEW_PADDING) {
                    IgnoreLayout = true,
                    Left = OVERVIEW_PADDING,
                    Bottom = OVERVIEW_PADDING,
                    HorizontalAlignment = ItemsAlignment.Center,
                    Children = [new TextNode(hint, theme.Text.Size, theme.Text.Color,
                        Math.Max(1, width - 2 * OVERVIEW_PADDING), TextWrapping.Ellipsis)],
                },
            ],
        };
        var pointerOverSearch = _searchHovered.Value;
#if DEBUG_OVERVIEW_ZOOM
        pointerOverSearch |= _zoomTestDragging.Value || _zoomTestHovered.Value;
#endif
        var activated = _drawer.Draw(renderer, new OverviewDrawContext(width, height, 0, 0,
            progress, transitionProgress, previewTransition, _open, pointerOverSearch, now, dt,
            _hyprland.FocusedAddress, _selected, _previews, _background.GetImage()));
        overlay.Opacity = progress;
        overlay.Draw(renderer, 0, 0);
        var pointerOverOverlay = _searchHovered.Value;
#if DEBUG_OVERVIEW_ZOOM
        pointerOverOverlay |= _zoomTestDragging.Value || _zoomTestHovered.Value;
#endif
        if (activated is not null && !pointerOverOverlay)
        {
            Activate(activated);
        }
    }

    private WindowThumbnailSnapshot? GetThumbnail(string address) =>
        _thumbnailIndices.TryGetValue(address, out var index) && index >= 0 ? _thumbnailSnapshot![index] : null;

    private bool UpdateThumbnailSnapshot(out bool metadataChanged)
    {
        metadataChanged = false;
        var thumbnails = _thumbnails.Snapshot;
        if (ReferenceEquals(thumbnails, _thumbnailSnapshot))
        {
            return false;
        }

        metadataChanged = _thumbnailSnapshot is null || thumbnails.Count != _thumbnailSnapshot.Count;
        for (var i = 0; !metadataChanged && i < thumbnails.Count; i++)
        {
            metadataChanged = !ReferenceEquals(thumbnails[i].Window, _thumbnailSnapshot![i].Window);
        }

        if (metadataChanged)
        {
            _thumbnailIndices.Clear();
            for (var i = 0; i < thumbnails.Count; i++)
            {
                var address = thumbnails[i].Window.Address;
                // Ambiguous addresses must neither demand capture nor display an arbitrary frame.
                if (!_thumbnailIndices.TryAdd(address, i))
                {
                    _thumbnailIndices[address] = -1;
                }
            }
        }

        _thumbnailSnapshot = thumbnails;
        return true;
    }

    private void BuildPreviews(int width, int height)
    {
        var workspaces = _hyprland.Snapshot.Workspaces;
        var windowsChanged = false;
        if (!ReferenceEquals(workspaces, _workspaceSnapshot))
        {
            var windows = workspaces
                .SelectMany(workspace => workspace.Windows.Select(window =>
                    new WindowIdentity(window.Address, window.Title, workspace.Id, workspace.MonitorName, window.Width, window.Height, window.ClassName, window.InitialClassName)))
                .Where(window => !string.IsNullOrEmpty(window.Address))
                .DistinctBy(window => window.Address).ToArray();
            windowsChanged = !_windows.AsSpan().SequenceEqual(windows);
            if (windowsChanged)
            {
                _windows = windows;
            }

            _workspaceSnapshot = workspaces;
        }

        var thumbnailsChanged = UpdateThumbnailSnapshot(out var metadataChanged);
        var updateDemand = windowsChanged || metadataChanged || _layoutDirty;
        if (updateDemand || thumbnailsChanged)
        {
            if (updateDemand)
            {
                _nextDemand.Clear();
            }

            foreach (var window in _windows)
            {
                var thumbnail = GetThumbnail(window.Address);
                if (updateDemand && thumbnail is not null)
                {
                    _nextDemand.Add(thumbnail.Window.Handle);
                }

                var aspect = window.Width > 0 && window.Height > 0
                    ? (float)window.Width / window.Height
                    : thumbnail?.Frame is { } frame
                        ? (float)frame.Image.Width / Math.Max(1, frame.Image.Height)
                        : 16f / 9;
                if (aspect != _aspects.GetValueOrDefault(window.Address, 16f / 9))
                {
                    _layoutDirty = true;
                }

                _aspects[window.Address] = aspect;
            }

            if (updateDemand && !_demand.SetEquals(_nextDemand))
            {
                (_demand, _nextDemand) = (_nextDemand, _demand);
                _thumbnails.SetOverviewVisible(this, _outputName, true, _demand);
            }
        }

        if (windowsChanged || _layoutDirty || width != _layoutWidth || height != _layoutHeight)
        {
            BuildLayout(width, height);
            thumbnailsChanged = true;
        }

        if (thumbnailsChanged)
        {
            foreach (var preview in _previews)
            {
                preview.Frame = GetThumbnail(preview.Address)?.Frame;
            }
        }
    }

    private void BuildLayout(int width, int height)
    {
        _layoutWidth = width;
        _layoutHeight = height;
        _layoutDirty = false;
        _previews.Clear();
        var ratios = _windows.Select(window => _aspects.GetValueOrDefault(window.Address, 16f / 9)).ToArray();
        var bounds = _drawer.BuildLayout(width, height, ratios);
        for (var i = 0; i < _windows.Length; i++)
        {
            var window = _windows[i];
            _previews.Add(new OverviewPreview(window.Address, window.Title,
                $"{window.MonitorName} · Workspace {window.WorkspaceId}", window.WorkspaceId, bounds[i],
                _icons.TryResolve(window.ClassName) ?? _icons.TryResolve(window.InitialClassName), window.Width, window.Height));
        }

        BuildNavigationGraph();
        if (!_previews.Any(preview => preview.Address == _selected))
        {
            _selected = (_previews.FirstOrDefault(preview => preview.WorkspaceId == _openedWorkspaceId)
                ?? _previews.FirstOrDefault())?.Address;
        }
    }

    private void BuildNavigationGraph()
    {
        foreach (var preview in _previews)
        {
            preview.Neighbors.Clear();
            foreach (var (key, horizontal, sign) in new[] { (DialogKey.Left, true, -1), (DialogKey.Right, true, 1), (DialogKey.Up, false, -1), (DialogKey.Down, false, 1) })
            {
                OverviewPreview? nearest = null;
                var bestDistance = float.MaxValue;
                foreach (var target in _previews.Where(x => !ReferenceEquals(preview, x)))
                {
                    var origin = _drawer.ProjectCenter(preview.Bounds, _layoutWidth, _layoutHeight);
                    var destination = _drawer.ProjectCenter(target.Bounds, _layoutWidth, _layoutHeight);
                    var dx = destination.X - origin.X;
                    var dy = destination.Y - origin.Y;
                    var forward = (horizontal ? dx : dy) * sign;
                    var lateral = MathF.Abs(horizontal ? dy : dx);
                    // Directional cones prevent a mostly vertical neighbor from stealing Left/Right.
                    if (forward <= 1 || lateral > forward)
                    {
                        continue;
                    }

                    var distance = dx * dx + dy * dy;
                    if (distance >= bestDistance)
                    {
                        continue;
                    }

                    nearest = target;
                    bestDistance = distance;
                }

                if (nearest is not null)
                {
                    preview.Neighbors[key] = nearest.Address;
                }
            }
        }
    }

    private async Task FocusAsync(string address)
    {
        try
        {
            if (!await _hyprctl.FocusWindowAsync(address, _lifetime.Token))
            {
                AppLogger.Warning("Overview", $"Could not focus window {address}");
            }
        }

        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }

        catch (Exception exception)
        {
            AppLogger.Warning("Overview", "Could not focus selected window", exception);
        }
    }

    private async Task InstallBindingAsync(CancellationToken token)
    {
        try
        {
            if (!await _hyprctl.Bind("SUPER + SUPER_L", RequestToggle, new HyprlandBindOptions(Release: true), token))
            {
                AppLogger.Warning("Overview", "Could not bind Super release");
            }
        }

        catch (OperationCanceledException) when (token.IsCancellationRequested) { }

        catch (Exception exception)
        {
            AppLogger.Warning("Overview", "Could not install Overview binding", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Close();
        _lifetime.Cancel();
        _search.Dispose();
        _searchUrls.Dispose();
        _background.Dispose();
        try
        {
            _bindingTask.Wait(TimeSpan.FromSeconds(2));
        }

        catch (Exception exception)
        {
            AppLogger.Warning("Overview", "Overview binding did not stop cleanly", exception);
        }

        _lifetime.Dispose();
    }
}

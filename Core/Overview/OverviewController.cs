using HyprNetShell.Core.Assets;
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
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Overview;

public sealed class OverviewController : IDisposable
{
    public const float THUMBNAILS_SCALE = 0.88f;
    private const float THUMBNAILS_HORIZONTAL_PADDING = 56f;
    private const float THUMBNAILS_TOP_PADDING = 160f;
    private const float THUMBNAILS_BOTTOM_PADDING = 80f;
    private const float THUMBNAILS_GAP = 24f;
    private const float THUMBNAIL_CAPTION_HEIGHT = 44f;
    private const float THUMBNAIL_ICON_SIZE = 52f;
    private const float THUMBNAIL_ICON_TOP = -12f;
    private const float WORKSPACE_BADGE_PADDING = 12f;
    private const int OVERVIEW_PADDING = 24;

    private readonly HyprlandService _hyprland;
    private readonly IHyprctl _hyprctl;
    private readonly IWindowThumbnailService _thumbnails;
    private readonly OverviewBackgroundService _background;
    private readonly TextInputCoordinator _searchInputs;
    private readonly UnifiedSearchTab _search;
    private readonly UrlLauncher _searchUrls;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _bindingTask;

    private int _toggleRequests;
    private bool _open;
    private double _transitionStart;
    private float _transitionFrom;
    private string? _selected;
    private readonly Dictionary<string, float> _aspects = [];
    private readonly Dictionary<string, float> _hover = [];
    private readonly List<Preview> _previews = [];
    private readonly AppIconResolver _icons = new();
    private IReadOnlyList<WorkspaceSnapshot>? _workspaceSnapshot;
    private WindowIdentity[] _windows = [];
    private IReadOnlyList<WindowThumbnailSnapshot>? _thumbnailSnapshot;
    private readonly Dictionary<string, int> _thumbnailIndices = new(StringComparer.Ordinal);
    private HashSet<ulong> _nextDemand = [];
    private int _layoutWidth = -1;
    private int _layoutHeight = -1;
    private bool _layoutDirty = true;
    private readonly Dictionary<(string Text, float Width, float Size), (string Text, float Width)> _captions = [];
    private double _lastDraw;

    private static double Now => (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    private readonly record struct WindowIdentity(string Address, string Title, int WorkspaceId, string MonitorName, int Width, int Height, string ClassName, string InitialClassName);
    private sealed record Preview(string Address, string Title, string Subtitle, int WorkspaceId, Rect Bounds, string? IconPath)
    {
        public WindowThumbnailFrame? Frame
        {
            get; set;
        }
        public Dictionary<DialogKey, string> Neighbors { get; } = [];
    }

    private string _outputName = "";
    private HashSet<ulong> _demand = [];
    private bool _disposed;

    private int _openedWorkspaceId;


    internal OverviewController(HyprlandService hyprland, IHyprctl hyprctl, IWindowThumbnailService thumbnails,
        Func<string?> currentWallpaper, ClipboardHistoryService clipboard)
    {
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

    public ulong? OwnerOutputId
    {
        get; private set;
    }
    public bool IsVisible => _open;
    public ulong? RenderOutputId
    {
        get
        {
            if (!_open && Now - _transitionStart >= .18)
            {
                OwnerOutputId = null;
                _previews.Clear();
                _hover.Clear();
                _thumbnailSnapshot = null;
                _thumbnailIndices.Clear();
                _captions.Clear();
            }
            return OwnerOutputId;
        }
    }

    private float Progress
    {
        get
        {
            var t = (float)Math.Clamp((Now - _transitionStart) / .18, 0, 1);
            t = 1 - MathF.Pow(1 - t, 3);
            return _transitionFrom + ((_open ? 1 : 0) - _transitionFrom) * t;
        }
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
            OwnerOutputId = outputId;
            _outputName = outputName;
            _transitionFrom = 0;
            _transitionStart = Now;
            _open = true;

            _openedWorkspaceId = snapshot.FocusedWorkspaceId;

            _search.ClearQuery();
            _search.Activate();
            _selected = null;
            _aspects.Clear();
            _hover.Clear();
            _workspaceSnapshot = null;
            _layoutDirty = true;
            _captions.Clear();
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
        Close();
        _ = FocusAsync(address);
    }

    public void Close()
    {
        if (_open)
        {
            _transitionFrom = Progress;
            _transitionStart = Now;
            _open = false;
        }

        _search.Deactivate();
        _demand.Clear();
        _thumbnails.RemoveOwner(this);
    }

    public Node Draw(int width, int height)
    {
        if (_open)
        {
            BuildPreviews(width, height);
        }

        return new OverviewNode(this, width, height, BuildBackground(width, height));
    }

    private Node BuildBackground(int width, int height) => new BackgroundNode(width, height, _background.GetImage());

    private sealed class BackgroundNode(int width, int height, RawImageData? image) : Node
    {
        public override int Width => width;
        public override int Height => height;

        public override void Draw(IRenderApi renderer, int x, int y)
        {
            var bounds = new Rect(x, y, width, height);
            if (image is null)
            {
                renderer.FillRect(bounds, ThemeManager.Current.Panel.PushOpacity(Opacity));
                return;
            }
            // Overview fills the output; the renderer viewport clips the centered cover image.
            var scale = Math.Max((float)width / image.Width, (float)height / image.Height);
            var w = image.Width * scale;
            var h = image.Height * scale;
            renderer.DrawImage(image, new Rect(x + (width - w) / 2, y + (height - h) / 2, w, h),
                Color.White.PushOpacity(Opacity));
            renderer.FillRect(bounds, Color.FromRgb(0, 0, 0, .48f * Opacity));
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
        _captions.Clear();
        _previews.Clear();
        var windows = _windows;
        if (windows.Length != 0)
        {
            var ratios = windows.Select(window => _aspects.GetValueOrDefault(window.Address, 16f / 9)).ToArray();
            var availableWidth = Math.Max(1, width - 2 * THUMBNAILS_HORIZONTAL_PADDING);
            var availableHeight = Math.Max(1, height - THUMBNAILS_TOP_PADDING - THUMBNAILS_BOTTOM_PADDING);

            List<List<int>> bestRows = [];
            float bestHeight = 0;
            // Try balanced contiguous rows, then choose the largest common preview height that fits both axes.
            for (var count = 1; count <= windows.Length; count++)
            {
                var rows = Enumerable.Range(0, count).Select(_ => new List<int>()).ToList();
                var remaining = ratios.Sum();
                var index = 0;
                for (var row = 0; row < count; row++)
                {
                    var target = remaining / (count - row);
                    float sum = 0;
                    while (index < windows.Length - (count - row - 1))
                    {
                        if (rows[row].Count > 0 && row < count - 1 && MathF.Abs(sum - target) < MathF.Abs(sum + ratios[index] - target))
                        {
                            break;
                        }

                        rows[row].Add(index);
                        sum += ratios[index++];
                    }
                    remaining -= sum;
                }
                var h = Math.Min(340, (availableHeight - (count - 1) * THUMBNAILS_GAP - count * THUMBNAIL_CAPTION_HEIGHT) / count);
                foreach (var row in rows)
                {
                    h = Math.Min(h, (availableWidth - (row.Count - 1) * THUMBNAILS_GAP) / row.Sum(i => ratios[i]));
                }

                if (h <= bestHeight)
                {
                    continue;
                }
                bestHeight = h;
                bestRows = rows;
            }
            if (bestRows.Count == 0)
            {
                return;
            }

            var y = THUMBNAILS_TOP_PADDING + (availableHeight - bestRows.Count * (bestHeight + THUMBNAIL_CAPTION_HEIGHT) - (bestRows.Count - 1) * THUMBNAILS_GAP) / 2;
            foreach (var row in bestRows)
            {
                var rowWidth = row.Sum(i => ratios[i] * bestHeight) + (row.Count - 1) * THUMBNAILS_GAP;
                var x = (width - rowWidth) / 2;
                foreach (var i in row)
                {
                    var window = windows[i];
                    var w = ratios[i] * bestHeight;
                    _previews.Add(new Preview(window.Address, window.Title,
                        $"{window.MonitorName} · Workspace {window.WorkspaceId}", window.WorkspaceId, new Rect(x, y, w, bestHeight),
                                                _icons.TryResolve(window.ClassName) ?? _icons.TryResolve(window.InitialClassName)));
                    x += w + THUMBNAILS_GAP;
                }
                y += bestHeight + THUMBNAIL_CAPTION_HEIGHT + THUMBNAILS_GAP;
            }
            BuildNavigationGraph();
            if (!_previews.Any(p => p.Address == _selected))
            {
                _selected = (_previews.FirstOrDefault(preview => preview.WorkspaceId == _openedWorkspaceId)
                                    ?? _previews.FirstOrDefault())?.Address;
            }
        }
        else
        {
            _selected = null;
            return;
        }
    }

    private void BuildNavigationGraph()
    {
        foreach (var preview in _previews)
        {
            preview.Neighbors.Clear();
            foreach (var (key, horizontal, sign) in new[] { (DialogKey.Left, true, -1), (DialogKey.Right, true, 1), (DialogKey.Up, false, -1), (DialogKey.Down, false, 1) })
            {
                Preview? nearest = null;
                var bestDistance = float.MaxValue;
                foreach (var target in _previews.Where(x => !ReferenceEquals(preview, x)))
                {
                    var dx = target.Bounds.X + target.Bounds.Width / 2 - preview.Bounds.X - preview.Bounds.Width / 2;
                    var dy = target.Bounds.Y + target.Bounds.Height / 2 - preview.Bounds.Y - preview.Bounds.Height / 2;
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

    private sealed class OverviewNode(OverviewController owner, int width, int height, Node background) : Node
    {
        public override int Width => width;

        public override int Height => height;

        public override void Draw(IRenderApi renderer, int x, int y)
        {
            var progress = owner.Progress;
            background.Opacity = progress;
            background.Draw(renderer, x, y);
            var now = Now;
            var dt = Math.Max(0, now - owner._lastDraw);
            owner._lastDraw = now;
            var theme = ThemeManager.Current;
            var searchWidth = Math.Max(1, Math.Min(720, width - 2 * OVERVIEW_PADDING));
            var search = new BoxNode(searchWidth) {
                Direction = Direction.Vertical,
                HorizontalAlignment = ItemsAlignment.Stretch,
                Children = [owner._search.DrawOverview(height - 2 * OVERVIEW_PADDING)],
            };

            var searchX = x + (width - searchWidth) / 2;
            var searchY = y + OVERVIEW_PADDING;
            var searchBounds = new Rect(searchX, searchY, search.Width, search.Height);
            var pointerOverSearch = Layout.Input.HasPointer && searchBounds.Contains(Layout.Input.PointerX, Layout.Input.PointerY);
            foreach (var preview in owner._previews)
            {
                var bounds = preview.Bounds;
                var hit = new Rect(x + bounds.X, y + bounds.Y, bounds.Width, bounds.Height + THUMBNAIL_CAPTION_HEIGHT);
                var hovered = owner._open && !pointerOverSearch && Layout.Input.HasPointer && hit.Contains(Layout.Input.PointerX, Layout.Input.PointerY);
                var hover = owner._hover.GetValueOrDefault(preview.Address);
                hover += ((hovered ? 1 : 0) - hover) * (float)(1 - Math.Exp(-dt / 0.07));
                owner._hover[preview.Address] = hover;
                var scale = THUMBNAILS_SCALE * (0.94f + 0.06f * progress + 0.035f * hover);
                var rect = new Rect(x + bounds.X + bounds.Width * (1 - scale) / 2,
                    y + bounds.Y + bounds.Height * (1 - scale) / 2 + 18 * (1 - progress) - 7 * hover,
                    bounds.Width * scale, bounds.Height * scale);

                renderer.FillRoundedShadow(rect, 6, Color.FromRgb(0, 0, 0, 0.35f * progress), 12);
                if (preview.Frame is { } frame)
                {
                    var fit = Math.Min(rect.Width / frame.Image.Width, rect.Height / frame.Image.Height);
                    var image = new Rect(rect.X + (rect.Width - frame.Image.Width * fit) / 2,
                        rect.Y + (rect.Height - frame.Image.Height * fit) / 2, frame.Image.Width * fit, frame.Image.Height * fit);
                    renderer.DrawRoundedImage(frame.Image, image, 6, Color.White.PushOpacity(progress));
                }
                else
                {
                    renderer.FillRoundedRect(rect, 6, theme.Panel.PushOpacity(progress));
                    DrawCaption(renderer, "Preview unavailable", rect.X, rect.Y + rect.Height / 2, rect.Width, theme.Text.SmallSize, theme.Text.MutedColor.PushOpacity(progress));
                }
                if (preview.Address == owner._selected)
                {
                    renderer.FillRoundedBorder(new Rect(rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8),
                                            10, theme.Border.Width, Color.White.PushOpacity(progress));
                }

                var iconSize = Math.Min(THUMBNAIL_ICON_SIZE * scale, rect.Width);
                var iconRect = new Rect(rect.X + (rect.Width - iconSize) / 2,
                    rect.Y + THUMBNAIL_ICON_TOP * scale, iconSize, iconSize);
                var iconShadow = Color.FromRgb(0, 0, 0, 0.45f * progress);
                if (preview.IconPath is { } iconPath)
                {
                    renderer.DrawImageShadow(iconPath, iconRect, iconShadow, 6 * scale,
                        offsetY: 2 * scale, loadAsync: true);
                    renderer.DrawImage(iconPath, iconRect, Color.White.PushOpacity(progress), loadAsync: true);
                }
                else
                {
                    renderer.DrawImageShadow(Icons.Application, iconRect, iconShadow, 6 * scale, offsetY: 2 * scale);
                    renderer.DrawImage(Icons.Application, iconRect, theme.Text.Color, opacity: progress);
                }

                var workspaceText = preview.WorkspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var workspaceTextWidth = renderer.MeasureText(workspaceText, theme.Text.HeaderSize);
                var badgeWidth = workspaceTextWidth + 20;
                var badgeHeight = theme.Text.HeaderSize + 12;
                var badgeX = rect.Width >= badgeWidth + 2 * WORKSPACE_BADGE_PADDING
                    ? rect.X + WORKSPACE_BADGE_PADDING
                    : rect.X + (rect.Width - badgeWidth) / 2;
                var badge = new Rect(badgeX, rect.Y + rect.Height - WORKSPACE_BADGE_PADDING - badgeHeight, badgeWidth, badgeHeight);
                renderer.FillRoundedRect(badge, 6, theme.Panel.PushOpacity(progress));
                renderer.FillRoundedBorder(badge, 6, 1, theme.Border.Color.PushOpacity(progress));
                renderer.DrawText(workspaceText, badge.X + (badgeWidth - workspaceTextWidth) / 2,
                    badge.Y + (badgeHeight + theme.Text.HeaderSize * 0.72f) / 2,
                    theme.Text.HeaderSize, theme.Text.Color.PushOpacity(progress));

                DrawCaption(renderer, preview.Title, rect.X, rect.Y + rect.Height + 20, rect.Width, theme.Text.Size, theme.Text.Color.PushOpacity(progress));
                DrawCaption(renderer, preview.Subtitle, rect.X, rect.Y + rect.Height + 38, rect.Width, theme.Text.SmallSize, theme.Text.MutedColor.PushOpacity(progress));
                if (hovered && Layout.Input.PointerPressed)
                {
                    owner.Activate(preview.Address);
                }
            }

            DrawCaption(renderer, owner._previews.Count == 0 ? "No windows · Type to search" : "Type to search · Arrow keys to select · Enter to focus · Esc to clear / close",
                x + OVERVIEW_PADDING, y + height - OVERVIEW_PADDING, width - 2 * OVERVIEW_PADDING, theme.Text.Size, theme.Text.Color.PushOpacity(progress));

            // BoxNode registers the fullscreen input region; custom Core nodes cannot access GUI's internal registration API.
            if (owner._open)
            {
                new BoxNode(width, height) { OnClick = () => { } }.Draw(renderer, x, y);
            }

            search.Opacity = progress;
            search.Draw(renderer, searchX, searchY);
        }

        private void DrawCaption(IRenderApi renderer, string text, float x, float y, float width, float size, Color color)
        {
            if (width <= 0)
            {
                return;
            }
            // Round down so cached fitting never overflows animated bounds, without subpixel hover churn.
            var fitWidth = MathF.Floor(width / 4) * 4;
            var key = (text, fitWidth, size);
            if (!owner._captions.TryGetValue(key, out var caption))
            {
                var measuredWidth = renderer.MeasureText(text, size);
                if (measuredWidth > fitWidth)
                {
                    var low = 0;
                    var high = text.Length;
                    while (low < high)
                    {
                        var mid = (low + high + 1) / 2;
                        if (renderer.MeasureText(text[..mid] + "…", size) <= fitWidth)
                        {
                            low = mid;
                        }
                        else
                        {
                            high = mid - 1;
                        }
                    }
                    text = text[..low] + "…";
                    measuredWidth = renderer.MeasureText(text, size);
                }

                caption = (text, measuredWidth);
                if (owner._captions.Count >= 2048)
                {
                    owner._captions.Clear();
                }

                owner._captions.Add(key, caption);
            }
            renderer.DrawText(caption.Text, x + Math.Max(0, (width - caption.Width) / 2), y, size, color);
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

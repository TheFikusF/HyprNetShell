using HyprNetShell.Application.Diagnostics;
using HyprNetShell.Application.LockScreen;
using HyprNetShell.Application.Screenshots;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Application;

internal static class ShellApplication
{
    private const int BAR_HEIGHT = 52;

    internal static int Run()
    {
        AppLogger.Initialize();
        try
        {
            return RunShell();
        }
        catch (Exception exception)
        {
            AppLogger.Error("Application", "Fatal error", exception);
            return 1;
        }
        finally
        {
            AppLogger.Shutdown();
        }
    }

    private static int RunShell()
    {
        using var layer = new HyprLayer(BAR_HEIGHT);
        if (!layer.MakeCurrent(0))
        {
            throw new InvalidOperationException("Failed to make the fallback EGL surface current.");
        }

        using var renderer = new Renderer((int)HyprLayer.TARGET_FRAMERATE, HyprLayer.GetProcAddress);
#if HYPRNETSHELL_PERFORMANCE_PROFILING
        using var performanceProfiler = PerformanceProfiler.TryCreate();
        renderer.SetDiagnosticsEnabled(performanceProfiler is not null);
        if (performanceProfiler is not null)
        {
            AppLogger.Info("Performance", $"Performance profiling enabled; writing {performanceProfiler.OutputPath}");
        }
#else
        PerformanceProfiler? performanceProfiler = null;
#endif

        var loop = new ShellLoop(layer, renderer, performanceProfiler, BAR_HEIGHT);
        try
        {
            return loop.Run();
        }
        finally
        {
            loop.Dispose();
            layer.MakeCurrent(0);
        }
    }
}

internal sealed class ShellLoop : IDisposable
{
    private readonly HyprLayer _layer;
    private readonly Renderer _renderer;
    private readonly PerformanceProfiler? _performanceProfiler;
    private readonly int _barHeight;
    private readonly StatusBarServices _services;
    private readonly ScreenshotController _screenshots = new();
    private readonly Dictionary<ulong, StatusBar> _views = [];
    private readonly Dictionary<ulong, SubmittedInputRegions> _submittedInputRegions = [];

    private sealed class SubmittedInputRegions
    {
        public int Width;
        public int Height;
        public readonly List<Rect> Regions = [];
    }
    private ulong? _focusedOutputId;
    private ulong? _dialogOwnerId;

    internal ShellLoop(
        HyprLayer layer,
        Renderer renderer,
        PerformanceProfiler? performanceProfiler,
        int barHeight)
    {
        _layer = layer;
        _services = new StatusBarServices(new WindowThumbnailBackend(layer));
        _renderer = renderer;
        _performanceProfiler = performanceProfiler;
        _barHeight = barHeight;
    }

    internal int Run()
    {
        while (RunFrame())
        {
        }

        return _layer.ReturnCode;
    }

    public void Dispose()
    {
        _services.Overview.Close();
        foreach (var view in _views.Values)
        {
            DisposeIfNeeded(view);
        }
        foreach (var outputId in _views.Keys)
        {
            Layout.RemoveOutput(outputId);
        }
        _views.Clear();
        _submittedInputRegions.Clear();
        DisposeIfNeeded(_services);
    }

    private bool RunFrame()
    {
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.Frame);
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.Update);
        var shouldContinue = _layer.Update();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.Update);
        if (!shouldContinue)
        {
            PerformanceProfiler.End(_performanceProfiler, PerformancePhase.Frame);
            return false;
        }

        ReconcileViews();
        var inputOwnerId = ResolveInputOwner();
        ProcessInput(inputOwnerId);
        if (_services.Overview.IsVisible)
        {
            _services.WindowThumbnails.Update();
        }

        RenderOutputs();
        _renderer.EndApplicationFrame();
        CompleteFrame();
        return true;
    }

    private void ReconcileViews()
    {
        if (!_layer.TopologyChanged)
        {
            return;
        }

        var currentOutputIds = _layer.Outputs.Select(output => output.Id).ToHashSet();
        foreach (var removedId in _views.Keys.Where(id => !currentOutputIds.Contains(id)).ToArray())
        {
            if (_services.Overview.OwnerOutputId == removedId)
            {
                _services.Overview.Close();
            }

            DisposeIfNeeded(_views[removedId]);
            _views.Remove(removedId);
            _submittedInputRegions.Remove(removedId);
            Layout.RemoveOutput(removedId);
        }

        foreach (var output in _layer.Outputs.Where(x => _views.ContainsKey(x.Id) == false))
        {
            _views.Add(output.Id, new StatusBar(_services, _renderer, _barHeight, () => output.Name));
        }

        if (_focusedOutputId is ulong focusedId && !currentOutputIds.Contains(focusedId))
        {
            _focusedOutputId = null;
        }

        if (_dialogOwnerId is ulong ownerId && !currentOutputIds.Contains(ownerId))
        {
            _dialogOwnerId = null;
        }
    }

    private ulong? ResolveInputOwner()
    {
        ulong? pointerOutputId = null;
        foreach (var output in _layer.Outputs.Where(x => x.Input.HasPointer))
        {
            pointerOutputId = output.Id;
            _focusedOutputId = output.Id;
        }

        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.RefreshState);
        _services.RefreshState();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.RefreshState);

        var focusedMonitorName = _services.FocusedMonitorName;
        var compositorFocusedOutputId = _layer.Outputs.FirstOrDefault(output =>
            string.Equals(output.Name, focusedMonitorName, StringComparison.Ordinal))?.Id;
        var fallbackOutputId = _layer.Outputs.Count > 0 ? _layer.Outputs[0].Id : (ulong?)null;
        return compositorFocusedOutputId ?? pointerOutputId ?? _focusedOutputId ?? fallbackOutputId;
    }

    private void ProcessInput(ulong? inputOwnerId)
    {
        _screenshots.TakeRequests(_services, inputOwnerId);
        foreach (var output in _layer.Outputs)
        {
            _screenshots.HandleInput(output);
        }

        _layer.SetScreenshotOverlay(_screenshots.SelectingOutputId ?? 0);

        var overview = _services.Overview;
        var ownerOutputName = _layer.Outputs.FirstOrDefault(output => output.Id == inputOwnerId)?.Name;
        overview.ProcessPendingRequests(inputOwnerId, ownerOutputName);
        var dialogs = _services.Dialogs;
        dialogs.ProcessPendingRequests();
        if (_screenshots.SelectingOutputId is not null || dialogs.IsVisible)
        {
            overview.Close();
        }

        if (overview.OwnerOutputId is ulong overviewOwner)
        {
            var output = _layer.Outputs.FirstOrDefault(output => output.Id == overviewOwner);
            if (output is not null)
            {
                overview.HandleInput(output.PressedKey, output.TextInput, output.ControlPressed);
            }
        }
        if (dialogs.IsVisible && _dialogOwnerId is null)
        {
            _dialogOwnerId = inputOwnerId;
        }

        if (!overview.IsVisible && _screenshots.SelectingOutputId is null && dialogs.IsOpen && _dialogOwnerId is ulong ownerId)
        {
            var ownerOutput = _layer.Outputs.FirstOrDefault(output => output.Id == ownerId);
            if (ownerOutput is not null)
            {
                dialogs.HandleInput(
                    ownerOutput.PressedKey,
                    ownerOutput.TextInput,
                    ownerOutput.Input.ScrollDelta,
                    ownerOutput.ControlPressed);
            }
        }

        _layer.SetKeyboardInteractiveBar((_services.Overview.IsVisible ? _services.Overview.OwnerOutputId : null) ?? (dialogs.IsOpen ? _dialogOwnerId ?? 0 : 0));
    }

    private void RenderOutputs()
    {
        foreach (var output in _layer.Outputs)
        {
            if (!_layer.MakeCurrent(output.Id))
            {
                if (_services.Overview.OwnerOutputId == output.Id)
                {
                    _services.Overview.Close();
                }

                continue;
            }

            RenderOutput(output);
            RenderScreenshotOverlay(output);
        }
    }

    private void RenderOutput(HyprLayer.Output output)
    {
        Layout.BeginDiagnosticsFrame(_performanceProfiler is not null);
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.BeginRender);
        _renderer.BeginFrame(output.Width, output.Height);
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.BeginRender);

        Layout.Input = _services.Overview.IsVisible || _screenshots.SelectingOutputId is not null
            ? LayoutInput.None : output.Input;
        Layout.BeginInputRegionFrame(output.Id);
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.DrawBar);
        _views[output.Id].Draw();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.DrawBar);

        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.DrawDialog);
        if (_dialogOwnerId == output.Id && _services.Dialogs.IsVisible)
        {
            using var dialogLayout = new Layout(
                _renderer,
                _renderer.Width,
                _renderer.Height,
                layer: RenderLayer.Dialog);
            dialogLayout.AddNode(_services.Dialogs.Draw());
        }
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.DrawDialog);

        if (_services.Overview.RenderOutputId == output.Id)
        {
            Layout.DrawOnLayer(RenderLayer.Dialog, _ => Layout.Input = _services.Overview.IsVisible ? output.Input : LayoutInput.None);
            using (var overviewLayout = new Layout(_renderer, output.Width, output.Height, layer: RenderLayer.Dialog))
            {
                overviewLayout.AddNode(_services.Overview.Draw(output.Width, output.Height));
            }
            Layout.DrawOnLayer(RenderLayer.Dialog, _ => Layout.Input = LayoutInput.None);
        }

        Layout.DrawLayers();
        Layout.Input = LayoutInput.None;

        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.SetInputRegions);
        SubmitInputRegionsIfChanged(output);
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.SetInputRegions);

        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.EndRender);
        _renderer.EndFrame();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.EndRender);

        PerformanceProfiler.AddFrameMetrics(
            _performanceProfiler,
            Layout.GetFrameMetrics(),
            _renderer.GetFrameMetrics());
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.SwapBuffers);
        _ = _layer.SwapBuffers(output.Id);
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.SwapBuffers);
    }

    private void SubmitInputRegionsIfChanged(HyprLayer.Output output)
    {
        var regions = Layout.GetInputRegions();
        if (_submittedInputRegions.TryGetValue(output.Id, out var submitted) &&
            submitted.Width == output.Width && submitted.Height == output.Height &&
            submitted.Regions.SequenceEqual(regions))
        {
            return;
        }

        _layer.SetInputRegions(output.Id, regions);
        if (submitted is null)
        {
            submitted = new SubmittedInputRegions();
            _submittedInputRegions.Add(output.Id, submitted);
        }
        submitted.Width = output.Width;
        submitted.Height = output.Height;
        submitted.Regions.Clear();
        submitted.Regions.AddRange(regions);
    }

    private void RenderScreenshotOverlay(HyprLayer.Output output)
    {
        if (!_screenshots.IsSelecting(output.Id) || !_layer.MakeScreenshotCurrent(output.Id))
        {
            return;
        }

        _renderer.BeginFrame(output.Width, output.Height);
        _screenshots.DrawOverlay(_renderer, output.Id);
        _renderer.EndFrame();
        _ = _layer.SwapScreenshotBuffers(output.Id);
    }

    private void CompleteFrame()
    {
        _screenshots.ProcessPendingCapture(_layer, _services);
        if (_services.TryTakeLockScreenRequest())
        {
            _services.Overview.Close();
            _layer.SetKeyboardInteractiveBar(0);
            LockScreenApplication.Start(_layer, _services);
        }

        var dialogs = _services.Dialogs;
        if (!dialogs.IsVisible)
        {
            _dialogOwnerId = null;
        }

        _layer.SetKeyboardInteractiveBar((_services.Overview.IsVisible ? _services.Overview.OwnerOutputId : null) ?? (dialogs.IsOpen ? _dialogOwnerId ?? 0 : 0));
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.PaceFrame);
        _layer.PaceFrame();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.PaceFrame);
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.Frame);
        PerformanceProfiler.CompleteFrame(_performanceProfiler);
    }

    private static void DisposeIfNeeded<T>(T instance)
    {
        if (instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

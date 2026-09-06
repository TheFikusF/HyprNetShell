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
    private const int BarHeight = 52;

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
        using var layer = new HyprLayer(BarHeight);
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

        var loop = new ShellLoop(layer, renderer, performanceProfiler, BarHeight);
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
    private readonly StatusBarServices _services = new();
    private readonly ScreenshotController _screenshots = new();
    private readonly Dictionary<ulong, StatusBar> _views = [];
    private ulong? _focusedOutputId;
    private ulong? _dialogOwnerId;

    internal ShellLoop(
        HyprLayer layer,
        Renderer renderer,
        PerformanceProfiler? performanceProfiler,
        int barHeight)
    {
        _layer = layer;
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
        foreach (var view in _views.Values)
        {
            DisposeIfNeeded(view);
        }
        _views.Clear();
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
        RenderOutputs();
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
            DisposeIfNeeded(_views[removedId]);
            _views.Remove(removedId);
        }

        foreach (var output in _layer.Outputs)
        {
            if (!_views.ContainsKey(output.Id))
            {
                _views.Add(output.Id, new StatusBar(_services, _renderer, _barHeight, () => output.Name));
            }
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
        foreach (var output in _layer.Outputs)
        {
            if (output.Input.HasPointer)
            {
                pointerOutputId = output.Id;
                _focusedOutputId = output.Id;
            }
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

        var dialogs = _services.Dialogs;
        dialogs.ProcessPendingRequests();
        if (dialogs.IsVisible && _dialogOwnerId is null)
        {
            _dialogOwnerId = inputOwnerId;
        }

        if (_screenshots.SelectingOutputId is null && dialogs.IsOpen && _dialogOwnerId is ulong ownerId)
        {
            var ownerOutput = _layer.Outputs.FirstOrDefault(output => output.Id == ownerId);
            if (ownerOutput is not null)
            {
                dialogs.HandleInput(
                    ownerOutput.PressedKey,
                    ownerOutput.TextInput,
                    ownerOutput.Input.ScrollDelta);
            }
        }

        _layer.SetKeyboardInteractiveBar(dialogs.IsOpen ? _dialogOwnerId ?? 0 : 0);
    }

    private void RenderOutputs()
    {
        foreach (var output in _layer.Outputs)
        {
            if (!_layer.MakeCurrent(output.Id))
            {
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

        Layout.Input = output.Input;
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

        Layout.DrawLayers();

        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.SetInputRegions);
        _layer.SetInputRegions(output.Id, Layout.GetInputRegions());
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
            LockScreenApplication.Start(_layer, _services);
        }

        var dialogs = _services.Dialogs;
        if (!dialogs.IsVisible)
        {
            _dialogOwnerId = null;
        }

        _layer.SetKeyboardInteractiveBar(dialogs.IsOpen ? _dialogOwnerId ?? 0 : 0);
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

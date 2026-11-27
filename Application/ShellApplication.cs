using System.Diagnostics;
using HyprNetShell.Application.Diagnostics;
using HyprNetShell.Application.LockScreen;
using HyprNetShell.Application.Screenshots;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Renderers;
using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Native.Managed;
using HyprNetShell.Native.Interop.Authorization;

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
    private sealed class SubmittedInputRegions
    {
        public readonly List<Rect> Regions = [];
        public int Width;
        public int Height;
    }

    private readonly HyprLayer _layer;
    private readonly Renderer _renderer;
    private readonly PerformanceProfiler? _performanceProfiler;
    private readonly int _barHeight;
    private readonly StatusBarServices _services;
    private readonly DesktopAuthorization _authorization;
    private readonly AuthorizationDialogController _authorizationDialog;
    private readonly ScreenshotController _screenshots = new();
    private readonly Dictionary<ulong, StatusBar> _views = [];
    private readonly Dictionary<ulong, SubmittedInputRegions> _submittedInputRegions = [];

    private readonly Dictionary<ulong, LayoutInput> _barHoverInputs = [];
    private ulong? _focusedOutputId;
    private ulong? _dialogOwnerId;
    private ulong? _authorizationOwnerId;
    private Process? _lockProcess;

    internal ShellLoop(
        HyprLayer layer,
        Renderer renderer,
        PerformanceProfiler? performanceProfiler,
        int barHeight)
    {
        _layer = layer;
        _authorization = new DesktopAuthorization(message => AppLogger.Warning("Authorization", message));
        try
        {
            _services = new StatusBarServices(
                new WindowThumbnailBackend(layer), layer.SetSensitiveClipboardAsync, authorization: _authorization);
            try
            {
                _authorizationDialog = new AuthorizationDialogController(_services.Dialogs, _authorization);
            }
            catch
            {
                _services.Dispose();
                throw;
            }
        }
        catch
        {
            _authorization.Dispose();
            throw;
        }

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
        try
        {
            _layer.SetKeyboardInteractiveBar(0);
            _authorizationDialog.RevokeAll();
            _services.Overview.Close();
            _layer.SetOverviewOverlay(0);
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
            _barHoverInputs.Clear();
        }
        finally
        {
            try
            {
                _services.Dispose();
            }
            finally
            {
                _lockProcess?.Dispose();
                _authorization.Dispose();
            }
        }
    }

    private bool RunFrame()
    {
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.Frame);
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.ActiveFrame);
        if (!UpdateAndProcessInput())
        {
            PerformanceProfiler.End(_performanceProfiler, PerformancePhase.ActiveFrame);
            PerformanceProfiler.End(_performanceProfiler, PerformancePhase.Frame);
            return false;
        }

        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.PollThumbnails);
        _services.WindowThumbnails.Update();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.PollThumbnails);

        RenderOutputs();
        _renderer.EndApplicationFrame();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.ActiveFrame);
        CompleteFrame();
        return true;
    }

    private bool UpdateAndProcessInput()
    {
        // Classify input using the owner from before polling, even if this frame revokes the prompt.
        var sensitiveOwnerId = _authorizationOwnerId;
        var previousPromptId = _authorizationDialog.PromptId;
        try
        {
            PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.Update);
            var shouldContinue = _layer.Update(sensitiveOwnerId);
            PerformanceProfiler.End(_performanceProfiler, PerformancePhase.Update);
            if (!shouldContinue)
            {
                return false;
            }

            ReconcileViews();
            if (_lockProcess is { HasExited: true })
            {
                _lockProcess.Dispose();
                _lockProcess = null;
            }

            if (_lockProcess is null)
            {
                _authorizationDialog.Update();
            }

            var inputOwnerId = ResolveInputOwner();
            ProcessInput(inputOwnerId, sensitiveOwnerId, previousPromptId);
            return true;
        }
        finally
        {
            _layer.ClearSensitiveTextInput();
        }
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
            _barHoverInputs.Remove(removedId);
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

        if (_authorizationOwnerId is ulong authorizationOwner && !currentOutputIds.Contains(authorizationOwner))
        {
            _authorizationDialog.Cancel();
            _authorizationOwnerId = null;
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

    private void ProcessInput(ulong? inputOwnerId, ulong? sensitiveOwnerId, ulong? previousPromptId)
    {
        var dialogs = _services.Dialogs;
        var overview = _services.Overview;
        if (_authorizationDialog.IsOpen)
        {
            _authorizationOwnerId ??= inputOwnerId;
            overview.Close();
            _layer.SetScreenshotOverlay(0);
            // Do not apply keystrokes collected before this prompt owned the keyboard.
            if (previousPromptId == _authorizationDialog.PromptId &&
                sensitiveOwnerId is ulong authorizationOwner && _authorizationOwnerId == authorizationOwner)
            {
                var ownerOutput = _layer.Outputs.FirstOrDefault(output => output.Id == authorizationOwner);
                if (ownerOutput is not null)
                {
                    _authorizationDialog.HandleSensitiveInput(
                        ownerOutput.PressedKey, _layer.SensitiveTextInput, ownerOutput.ControlPressed);
                }
            }

            UpdateKeyboardOwner();
            return;
        }

        _authorizationOwnerId = null;
        if (sensitiveOwnerId is not null)
        {
            // Input was collected before releasing the authorization surface's keyboard focus.
            UpdateKeyboardOwner();
            return;
        }

        _screenshots.TakeRequests(_services, inputOwnerId);
        foreach (var output in _layer.Outputs)
        {
            _screenshots.HandleInput(output);
        }

        _layer.SetScreenshotOverlay(_screenshots.SelectingOutputId ?? 0);

        var ownerOutputName = _layer.Outputs.FirstOrDefault(output => output.Id == inputOwnerId)?.Name;
        overview.ProcessPendingRequests(inputOwnerId, ownerOutputName);
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

        UpdateKeyboardOwner();
    }

    private void UpdateKeyboardOwner()
    {
        var owner = _authorizationDialog.IsOpen ? _authorizationOwnerId
            : _screenshots.SelectingOutputId is not null ? null
            : _services.Overview.IsVisible ? _services.Overview.OwnerOutputId
            : _services.Dialogs.IsOpen ? _dialogOwnerId : null;
        _layer.SetKeyboardInteractiveBar(owner ?? 0);
    }

    private void RenderOutputs()
    {
        _layer.SetOverviewOverlay(_authorizationDialog.IsOpen ? 0 : _services.Overview.RenderOutputId ?? 0);
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
            if (!_authorizationDialog.IsOpen)
            {
                RenderScreenshotOverlay(output);
            }
        }
    }

    private void RenderOutput(HyprLayer.Output output)
    {
        Layout.BeginDiagnosticsFrame(_performanceProfiler is not null);
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.BeginRender);
        _renderer.BeginFrame(output.Width, output.Height);
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.BeginRender);

        var authorizationOpen = _authorizationDialog.IsOpen;
        var barInput = _services.Overview.IsVisible || _screenshots.SelectingOutputId is not null
            ? LayoutInput.None : output.Input;
        if (authorizationOpen)
        {
            // Hold bar hover in place while the real pointer is routed to the dialog.
            barInput = _barHoverInputs.GetValueOrDefault(output.Id, LayoutInput.None);
        }
        else
        {
            _barHoverInputs[output.Id] = barInput with { PointerDown = false, PointerPressed = false, ScrollDelta = 0 };
        }

        Layout.Input = barInput;
        Layout.BeginInputRegionFrame(output.Id);
        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.DrawBar);
        _views[output.Id].Draw();
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.DrawBar);

        PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.DrawDialog);
        var dialogOwnerId = authorizationOpen ? _authorizationOwnerId : _dialogOwnerId;
        if (dialogOwnerId == output.Id && _services.Dialogs.IsVisible)
        {
            if (authorizationOpen)
            {
                Layout.DrawOnLayer(RenderLayer.Dialog, _ => Layout.Input = output.Input);
            }

            using var dialogLayout = new Layout(
                _renderer,
                _renderer.Width,
                _renderer.Height,
                layer: RenderLayer.Dialog);
            dialogLayout.AddNode(_services.Dialogs.Draw());
        }
        PerformanceProfiler.End(_performanceProfiler, PerformancePhase.DrawDialog);

        if (!authorizationOpen && _services.Overview.RenderOutputId == output.Id)
        {
            Layout.DrawOnLayer(RenderLayer.Dialog, _ => Layout.Input = _services.Overview.IsVisible ? output.Input : LayoutInput.None);
            Layout.DrawOnLayer(RenderLayer.Dialog, renderer =>
            {
                PerformanceProfiler.Begin(_performanceProfiler, PerformancePhase.DrawOverview);
                _services.Overview.Draw(renderer, output.Width, output.Height);
                PerformanceProfiler.End(_performanceProfiler, PerformancePhase.DrawOverview);
            });
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
            _authorizationDialog.RevokeAll();
            _services.Overview.Close();
            _layer.SetKeyboardInteractiveBar(0);
            if (_lockProcess is null)
            {
                _lockProcess = LockScreenApplication.Start(_layer, _services);
            }
        }
        else
        {
            if (!_authorizationDialog.IsOpen && !_services.Dialogs.IsVisible)
            {
                _dialogOwnerId = null;
            }

            UpdateKeyboardOwner();
        }

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

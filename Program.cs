using HyprNetShell;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;

const int BAR_HEIGHT = 52;

if (args is ["--launch-desktop-entry", var desktopFile])
{
    return DesktopEntryLauncher.Launch(desktopFile);
}

if (args is ["--launch-desktop-action", var actionDesktopFile, var actionId])
{
    return DesktopEntryLauncher.LaunchAction(actionDesktopFile, actionId);
}

AppLogger.Initialize();
try
{
    using var layer = new HyprLayer(BAR_HEIGHT);
    if (!layer.MakeCurrent(0))
    {
        throw new InvalidOperationException("Failed to make the fallback EGL surface current.");
    }

    using var renderer = new Renderer(HyprLayer.GetProcAddress);
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

    var services = new StatusBarServices();
    var mainDialog = services.MainDialog;
    var views = new Dictionary<ulong, StatusBar>();
    ulong? focusedOutputId = null;
    ulong? dialogOwnerId = null;
    var launcherTogglePending = false;

    try
    {
        while (true)
        {
            PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.Frame);
            PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.Update);
            var shouldContinue = layer.Update();
            PerformanceProfiler.End(performanceProfiler, PerformancePhase.Update);
            if (!shouldContinue)
            {
                PerformanceProfiler.End(performanceProfiler, PerformancePhase.Frame);
                break;
            }

            if (layer.TopologyChanged)
            {
                var currentOutputIds = layer.Outputs.Select(output => output.Id).ToHashSet();
                foreach (var removedId in views.Keys.Where(id => !currentOutputIds.Contains(id)).ToArray())
                {
                    DisposeIfNeeded(views[removedId]);
                    views.Remove(removedId);
                }

                foreach (var output in layer.Outputs)
                {
                    if (!views.ContainsKey(output.Id))
                    {
                        views.Add(output.Id, new StatusBar(services, renderer, BAR_HEIGHT, () => output.Name));
                    }
                }

                if (focusedOutputId is ulong focusedId && !currentOutputIds.Contains(focusedId))
                {
                    focusedOutputId = null;
                }

                if (dialogOwnerId is ulong ownerId && !currentOutputIds.Contains(ownerId))
                {
                    dialogOwnerId = null;
                }
            }

            ulong? pointerOutputId = null;
            foreach (var output in layer.Outputs)
            {
                if (output.Input.HasPointer)
                {
                    pointerOutputId = output.Id;
                    focusedOutputId = output.Id;
                }
            }

            PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.RefreshState);
            services.RefreshState();
            PerformanceProfiler.End(performanceProfiler, PerformancePhase.RefreshState);

            var focusedMonitorName = services.FocusedMonitorName;
            var compositorFocusedOutputId = layer.Outputs.FirstOrDefault(output =>
                string.Equals(output.Name, focusedMonitorName, StringComparison.Ordinal))?.Id;
            var fallbackOutputId = layer.Outputs.Count > 0 ? layer.Outputs[0].Id : (ulong?)null;
            var inputOwnerId = compositorFocusedOutputId ?? pointerOutputId ?? focusedOutputId ?? fallbackOutputId;

            if (mainDialog.IsVisible && dialogOwnerId is null)
            {
                dialogOwnerId = inputOwnerId;
            }

            launcherTogglePending |= services.ConsumeLauncherToggleRequested();
            if (launcherTogglePending && inputOwnerId is ulong toggleTargetId)
            {
                if (!mainDialog.IsOpen)
                {
                    dialogOwnerId = toggleTargetId;
                }

                mainDialog.Toggle();
                launcherTogglePending = false;
            }

            if (mainDialog.IsOpen && dialogOwnerId is ulong currentOwnerId)
            {
                var ownerOutput = layer.Outputs.FirstOrDefault(output => output.Id == currentOwnerId);
                if (ownerOutput is not null)
                {
                    mainDialog.HandleInput(
                        ownerOutput.PressedKey,
                        ownerOutput.TextInput,
                        ownerOutput.Input.ScrollDelta);
                }
            }

            layer.SetKeyboardInteractiveBar(mainDialog.IsOpen ? dialogOwnerId ?? 0 : 0);

            foreach (var output in layer.Outputs)
            {
                if (!layer.MakeCurrent(output.Id))
                {
                    continue;
                }

                Layout.BeginDiagnosticsFrame(performanceProfiler is not null);
                PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.BeginRender);
                renderer.BeginFrame(output.Width, output.Height);
                PerformanceProfiler.End(performanceProfiler, PerformancePhase.BeginRender);

                Layout.Input = output.Input;
                Layout.BeginInputRegionFrame();
                PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.DrawBar);
                views[output.Id].Draw();
                PerformanceProfiler.End(performanceProfiler, PerformancePhase.DrawBar);

                PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.DrawDialog);
                if (dialogOwnerId == output.Id && mainDialog.IsVisible)
                {
                    using var dialogLayout = new Layout(renderer, renderer.Width, renderer.Height);
                    dialogLayout.AddNode(mainDialog.Draw());
                }
                PerformanceProfiler.End(performanceProfiler, PerformancePhase.DrawDialog);

                PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.SetInputRegions);
                layer.SetInputRegions(output.Id, Layout.GetInputRegions());
                PerformanceProfiler.End(performanceProfiler, PerformancePhase.SetInputRegions);

                PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.EndRender);
                renderer.EndFrame();
                PerformanceProfiler.End(performanceProfiler, PerformancePhase.EndRender);

                PerformanceProfiler.AddFrameMetrics(
                    performanceProfiler,
                    Layout.GetFrameMetrics(),
                    renderer.GetFrameMetrics());
                PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.SwapBuffers);
                _ = layer.SwapBuffers(output.Id);
                PerformanceProfiler.End(performanceProfiler, PerformancePhase.SwapBuffers);
            }

            if (!mainDialog.IsVisible)
            {
                dialogOwnerId = null;
            }

            layer.SetKeyboardInteractiveBar(mainDialog.IsOpen ? dialogOwnerId ?? 0 : 0);
            PerformanceProfiler.Begin(performanceProfiler, PerformancePhase.PaceFrame);
            layer.PaceFrame();
            PerformanceProfiler.End(performanceProfiler, PerformancePhase.PaceFrame);
            PerformanceProfiler.End(performanceProfiler, PerformancePhase.Frame);
            PerformanceProfiler.CompleteFrame(performanceProfiler);
        }

        return layer.ReturnCode;
    }
    finally
    {
        foreach (var view in views.Values)
        {
            DisposeIfNeeded(view);
        }

        DisposeIfNeeded(services);
        layer.MakeCurrent(0);
    }
}
catch (Exception e)
{
    AppLogger.Error("Application", "Fatal error", e);
    return 1;
}
finally
{
    AppLogger.Shutdown();
}

static void DisposeIfNeeded<T>(T instance)
{
    if (instance is IDisposable disposable)
    {
        disposable.Dispose();
    }
}

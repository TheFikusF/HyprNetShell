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
    var services = new StatusBarServices();
    var mainDialog = services.MainDialog;
    var views = new Dictionary<ulong, StatusBar>();
    ulong? focusedOutputId = null;
    ulong? dialogOwnerId = null;
    var launcherTogglePending = false;

    try
    {
        while (layer.Update())
        {
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

            services.RefreshState();
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

                renderer.BeginFrame(output.Width, output.Height);
                Layout.Input = output.Input;
                Layout.BeginInputRegionFrame();
                views[output.Id].Draw();
                if (dialogOwnerId == output.Id && mainDialog.IsVisible)
                {
                    using var dialogLayout = new Layout(renderer, renderer.Width, renderer.Height);
                    dialogLayout.AddNode(mainDialog.Draw());
                }

                layer.SetInputRegions(output.Id, Layout.GetInputRegions());
                renderer.EndFrame();
                _ = layer.SwapBuffers(output.Id);
            }

            if (!mainDialog.IsVisible)
            {
                dialogOwnerId = null;
            }

            layer.SetKeyboardInteractiveBar(mainDialog.IsOpen ? dialogOwnerId ?? 0 : 0);
            layer.PaceFrame();
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

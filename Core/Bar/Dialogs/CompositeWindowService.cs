using HyprNetShell.Core.Bar.MainDialogTabs;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;

namespace HyprNetShell.Core.Bar.Dialogs;

internal sealed class CompositeWindowService : IDisposable
{
    private readonly CompositeWindowConfiguration _configuration;
    private readonly TabsService _tabs;
    private readonly DialogService _dialogs;
    private readonly IHyprctl _hyprctl;
    private CancellationTokenSource _bindingLifetime = new();
    private Task _bindingTask = Task.CompletedTask;
    private bool _disposed;

    internal CompositeWindowService(
        CompositeWindowConfiguration configuration,
        TabsService tabs,
        DialogService dialogs,
        IHyprctl hyprctl)
    {
        _configuration = configuration;
        _tabs = tabs;
        _dialogs = dialogs;
        _hyprctl = hyprctl;

        _configuration.Changed += RefreshBindings;
        RefreshBindings();
    }

    private void RefreshBindings()
    {
        _bindingLifetime.Cancel();
        _bindingLifetime.Dispose();
        _bindingLifetime = new CancellationTokenSource();
        _bindingTask = BindWindowsAsync(_bindingLifetime.Token);
    }

    private async Task BindWindowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var definition in _configuration.Windows.Where(window => window.Hotkey.Length > 0))
            {
                var windowTabs = _tabs.Resolve(definition.TabIds);
                if (windowTabs.Count == 0)
                {
                    continue;
                }

                if (!await _hyprctl.Bind(
                        definition.Hotkey,
                        () => _dialogs.RequestOpen<CompositeWindow>(windowTabs),
                        new HyprlandBindOptions(Transparent: true),
                        cancellationToken))
                {
                    AppLogger.Warning(
                        "CompositeWindows",
                        $"Could not bind '{definition.Hotkey}' for '{definition.Name}'");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning("CompositeWindows", "Could not install composite window hotkeys", exception);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configuration.Changed -= RefreshBindings;
        _bindingLifetime.Cancel();
        try
        {
            _bindingTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("CompositeWindows", "Hotkey bindings did not stop cleanly", exception);
        }

        _bindingLifetime.Dispose();
    }
}

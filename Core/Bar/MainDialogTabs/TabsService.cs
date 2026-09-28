using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;
using HyprNetShell.Core.Platform;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class TabsService : IDisposable
{
    private readonly IMainDialogTab[] _tabs;
    private readonly IReadOnlyDictionary<string, IMainDialogTab> _tabsById;

    internal IReadOnlyList<IMainDialogTab> Tabs => _tabs;
    internal TextInputCoordinator Inputs { get; }

    internal TabsService(
        ClipboardHistoryService clipboardHistory,
        IHyprctl hyprctl,
        UrlLauncher urlLauncher,
        NetworkModuleService network,
        BluetoothModuleService bluetooth,
        WallpaperModuleService wallpapers,
        WeatherService weather,
        CalendarService calendar,
        DictionaryService dictionary,
        Action closeDialog,
        Theme theme)
    {
        Inputs = new TextInputCoordinator(clipboardHistory, theme);
        _tabs =
        [
            new UnifiedSearchTab(hyprctl, urlLauncher, closeDialog, clipboardHistory, Inputs, theme),
            new ApplicationLauncherTab(hyprctl, closeDialog, Inputs, theme),
            new CalculatorTab(clipboardHistory, Inputs, theme),
            new DictionaryTab(dictionary, clipboardHistory, Inputs, theme),
            new WorldClockTab(Inputs, theme),
            new CalendarTab(calendar, theme),
            new TetrisTab(theme),
            new ClipboardManagerTab(clipboardHistory, closeDialog, Inputs, theme),
            new WallpapersTab(wallpapers, closeDialog, Inputs, theme),
            new WifiTab(network, Inputs, theme),
            new BluetoothTab(bluetooth, theme),
            new WeatherTab(weather, theme),
        ];
        _tabsById = _tabs.ToDictionary(tab => tab.Id, StringComparer.Ordinal);
    }

    internal T Get<T>() where T : class, IMainDialogTab => _tabs.OfType<T>().Single();

    internal IReadOnlyList<IMainDialogTab> Resolve(IEnumerable<string> tabIds) => tabIds
        .Distinct(StringComparer.Ordinal)
        .Select(id => _tabsById.GetValueOrDefault(id))
        .Where(tab => tab is not null)
        .Cast<IMainDialogTab>()
        .ToArray();

    public void Dispose()
    {
        foreach (var tab in _tabs)
        {
            if (tab is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

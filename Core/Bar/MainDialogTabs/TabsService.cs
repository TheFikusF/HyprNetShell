using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.System;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class TabsService : IDisposable
{
    private readonly IMainDialogTab[] _tabs;
    private readonly IReadOnlyDictionary<string, IMainDialogTab> _tabsById;

    internal IReadOnlyList<IMainDialogTab> Tabs => _tabs;

    internal TabsService(
        ClipboardHistoryService clipboardHistory,
        IHyprctl hyprctl,
        NetworkModuleService network,
        WallpaperModuleService wallpapers,
        WeatherService weather,
        DictionaryService dictionary,
        Action closeDialog,
        Theme theme)
    {
        _tabs =
        [
            new UnifiedSearchTab(hyprctl, closeDialog, theme),
            new ApplicationLauncherTab(hyprctl, closeDialog, theme),
            new CalculatorTab(),
            new DictionaryTab(dictionary, theme),
            new WorldClockTab(theme),
            new ClipboardManagerTab(clipboardHistory, closeDialog, theme),
            new WallpapersTab(wallpapers, closeDialog, theme),
            new WifiTab(network, theme),
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

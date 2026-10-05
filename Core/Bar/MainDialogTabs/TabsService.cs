using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Features.KdeConnect;
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
        KdeConnectService kdeConnect,
        IHyprctl hyprctl,
        UrlLauncher urlLauncher,
        NetworkModuleService network,
        BluetoothModuleService bluetooth,
        WallpaperModuleService wallpapers,
        WeatherService weather,
        CalendarService calendar,
        DictionaryService dictionary,
        Action closeDialog)
    {
        Inputs = new TextInputCoordinator(clipboardHistory);
        _tabs =
        [
            new UnifiedSearchTab(hyprctl, urlLauncher, closeDialog, clipboardHistory, Inputs),
            new ApplicationLauncherTab(hyprctl, closeDialog, Inputs),
            new CalculatorTab(clipboardHistory, Inputs),
            new DictionaryTab(dictionary, clipboardHistory, Inputs),
            new WorldClockTab(Inputs),
            new CalendarTab(calendar),
            new TetrisTab(),
            new SolitaireTab(),
            new MinesweeperTab(),
            new ClipboardManagerTab(clipboardHistory, kdeConnect, closeDialog, Inputs),
            new KdeConnectTab(kdeConnect),
            new WallpapersTab(wallpapers, closeDialog, Inputs),
            new WifiTab(network, Inputs),
            new BluetoothTab(bluetooth),
            new WeatherTab(weather),
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
        foreach (var tab in _tabs.OfType<IDisposable>())
        {
            tab.Dispose();
        }
    }
}

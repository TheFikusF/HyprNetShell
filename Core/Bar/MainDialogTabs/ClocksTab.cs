using HyprNetShell.Core.Bar.MainDialogTabs.Clocks;
using HyprNetShell.Core.Bar.MainDialogTabs.Clocks.WorldClocks;
using HyprNetShell.Core.Bar.MainDialogTabs.Clocks.Alarm;
using HyprNetShell.Core.Bar.MainDialogTabs.Clocks.Timer;
using HyprNetShell.Core.Bar.MainDialogTabs.Clocks.Stopwatch;
using HyprNetShell.Core.Assets;
using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.System;
using HyprNetShell.GUI.Layout;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal sealed class ClocksTab : IMainDialogTab
{
    private readonly SegmentedSwitch _pageSwitch = new();
    private readonly WorldClocksSubtab _worldClocks;
    private readonly IClocksSubtab[] _subtabs;
    private int _page;

    public string Id => "world-clocks";
    public string Title => "Clocks";
    public SvgAsset Icon => Icons.Clock;

    public ClocksTab(TextInputCoordinator inputs) : this(WorldClockService.Shared, inputs)
    {
    }

    public ClocksTab(WorldClockService clocks, TextInputCoordinator inputs)
    {
        _worldClocks = new WorldClocksSubtab(clocks, inputs);
        _subtabs = [_worldClocks, new AlarmSubtab(inputs), new TimerSubtab(inputs), new StopwatchSubtab(inputs)];
    }

    public void Activate()
    {
        _worldClocks.ResetSearch();
        _subtabs[_page].Activate();
    }

    public void MoveSelection(SelectionDirection direction) => _subtabs[_page].MoveSelection(direction);

    public void ActivateSelection() => _subtabs[_page].ActivateSelection();

    public Node Draw() => new BoxNode(new Style { Spacing = 12 }) {
        Direction = Direction.Vertical,
        HorizontalAlignment = ItemsAlignment.Stretch,
        Children =
        [
            _pageSwitch.Build(
            [
                new("0", ModulesCommon.BuildTextWithIcon(Icons.Clock, "World clocks")),
                new("1", ModulesCommon.BuildTextWithIcon(Icons.AlarmClock, "Alarm")),
                new("2", ModulesCommon.BuildTextWithIcon(Icons.Timer, "Timer")),
                new("3", ModulesCommon.BuildTextWithIcon(Icons.Stopwatch, "Stopwatch")),
            ], _page.ToString(), id => SelectPage(int.Parse(id))),
            _subtabs[_page].Draw(),
        ],
    };

    private void SelectPage(int page)
    {
        _page = page;
        _subtabs[_page].Activate();
    }
}

using HyprNetShell.Core.Bar.Common;
using HyprNetShell.Core.Features.Hyprland;
using HyprNetShell.Core.Logging;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Bar.MainDialogTabs;

internal enum ApplicationSelectionColumn
{
    Default,
    Actions,
}

internal sealed class ApplicationButtonState : ModulesCommon.BoxState
{
    internal int ActionIndex { get; set; }
    internal Dictionary<int, ModulesCommon.BoxState> Actions { get; } = [];
}

internal static class DesktopApplicationLaunch
{
    internal static async Task<bool> TryLaunchAsync(
        IHyprctl hyprctl,
        DesktopApplication application,
        DesktopAction? action,
        string logCategory)
    {
        try
        {
            return action is null
                ? await hyprctl.LaunchDesktopEntryAsync(application.DesktopFile)
                : await hyprctl.LaunchDesktopActionAsync(application.DesktopFile, action.Id);
        }
        catch (Exception exception)
        {
            var target = action is null
                ? $"desktop entry {application.DesktopFile}"
                : $"desktop action {action.Id} from {application.DesktopFile}";
            AppLogger.Error(logCategory, $"Could not launch {target}", exception);
            return false;
        }
    }
}

internal sealed class ApplicationResultInteraction(Theme theme)
{
    private readonly AppIconResolver _icons = new();
    private readonly Dictionary<int, ApplicationButtonState> _states = [];
    private ApplicationSelectionColumn _selectedColumn;

    internal void Reset()
    {
        _selectedColumn = ApplicationSelectionColumn.Default;
        _states.Clear();
    }

    internal void Normalize(int selectedIndex, DesktopApplication? application)
    {
        if (application is null || application.Actions.Count == 0)
        {
            _selectedColumn = ApplicationSelectionColumn.Default;
            if (_states.TryGetValue(selectedIndex, out var emptyState))
            {
                emptyState.ActionIndex = 0;
            }
            return;
        }

        var state = State(selectedIndex);
        if (state.ActionIndex < 0 || state.ActionIndex >= application.Actions.Count)
        {
            state.ActionIndex = 0;
        }
    }

    internal void MoveHorizontal(int selectedIndex, DesktopApplication application, SelectionDirection direction)
    {
        if (direction is not (SelectionDirection.Left or SelectionDirection.Right))
        {
            return;
        }

        var state = State(selectedIndex);
        if (application.Actions.Count == 0)
        {
            state.ActionIndex = 0;
            _selectedColumn = ApplicationSelectionColumn.Default;
            return;
        }

        if (_selectedColumn == ApplicationSelectionColumn.Actions)
        {
            state.ActionIndex += direction == SelectionDirection.Right ? 1 : -1;
        }

        (state.ActionIndex, _selectedColumn) = direction switch
        {
            SelectionDirection.Right when
                _selectedColumn == ApplicationSelectionColumn.Actions &&
                state.ActionIndex >= application.Actions.Count =>
                (0, ApplicationSelectionColumn.Default),
            SelectionDirection.Left when
                _selectedColumn == ApplicationSelectionColumn.Actions &&
                state.ActionIndex < 0 =>
                (0, ApplicationSelectionColumn.Default),
            SelectionDirection.Left when _selectedColumn == ApplicationSelectionColumn.Default =>
                (application.Actions.Count - 1, ApplicationSelectionColumn.Actions),
            SelectionDirection.Right when _selectedColumn == ApplicationSelectionColumn.Default =>
                (0, ApplicationSelectionColumn.Actions),
            _ => (state.ActionIndex, _selectedColumn),
        };
    }

    internal DesktopAction? SelectedAction(int selectedIndex, DesktopApplication application)
    {
        var state = State(selectedIndex);
        return _selectedColumn == ApplicationSelectionColumn.Actions &&
               state.ActionIndex >= 0 &&
               state.ActionIndex < application.Actions.Count
            ? application.Actions[state.ActionIndex]
            : null;
    }

    internal BoxNode BuildRow(
        DesktopApplication application,
        int index,
        bool selected,
        Action activateDefault,
        Action<DesktopAction> activateAction)
    {
        var state = State(index);
        return ApplicationLauncherTab.BuildApplicationRow(
            application,
            selected,
            _selectedColumn,
            state,
            _icons,
            theme,
            () =>
            {
                _selectedColumn = ApplicationSelectionColumn.Default;
                activateDefault();
            },
            actionIndex =>
            {
                _selectedColumn = ApplicationSelectionColumn.Actions;
                state.ActionIndex = actionIndex;
                activateAction(application.Actions[actionIndex]);
            });
    }

    private ApplicationButtonState State(int index) => _states.GetState(index, theme.Panel);
}

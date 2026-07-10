using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Services;

public sealed class BarStateBuilder
{
    private readonly List<WorkspaceSnapshot> _workspaces = [];
    private readonly List<MonitorWorkspaceSnapshot> _monitorWorkspaces = [];
    private readonly List<BarModuleSnapshot> _centerModules = [];
    private readonly List<BarModuleSnapshot> _rightModules = [];
    private readonly List<TrayItemSnapshot> _trayItems = [];

    public string FocusedTitle { get; set; } = "Desktop";
    public string FocusedClassName { get; set; } = "";
    public int FocusedWorkspaceId { get; set; } = 1;
    public SystemStatsSnapshot SystemStats { get; set; } = SystemStatsSnapshot.Empty;
    public NetworkSnapshot Network { get; set; } = NetworkSnapshot.Empty;
    public AudioSnapshot Audio { get; set; } = AudioSnapshot.Empty;
    public BluetoothSnapshot Bluetooth { get; set; } = BluetoothSnapshot.Empty;
    public NotificationsSnapshot Notifications { get; set; } = NotificationsSnapshot.Empty;
    public PopupSnapshot? OpenPopup { get; set; }

    public void AddWorkspace(WorkspaceSnapshot workspace) => _workspaces.Add(workspace);
    public void AddMonitorWorkspaces(MonitorWorkspaceSnapshot monitor) => _monitorWorkspaces.Add(monitor);
    public void AddCenterModule(BarModuleSnapshot module) => _centerModules.Add(module);
    public void AddRightModule(BarModuleSnapshot module) => _rightModules.Add(module);
    public void AddTrayItem(TrayItemSnapshot item) => _trayItems.Add(item);

    public BarSnapshot Build()
    {
        return new BarSnapshot(
            _workspaces.OrderBy(workspace => workspace.Id).ToArray(),
            _monitorWorkspaces.ToArray(),
            FocusedTitle,
            FocusedClassName,
            FocusedWorkspaceId,
            SystemStats,
            Network,
            Audio,
            Bluetooth,
            Notifications,
            _centerModules.ToArray(),
            _rightModules.ToArray(),
            _trayItems.ToArray(),
            OpenPopup);
    }
}

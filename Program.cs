using HyprNetShell;
using HyprNetShell.Application;
using HyprNetShell.Application.LockScreen;

return args switch
{
    ["--launch-desktop-entry", var desktopFile] => DesktopEntryLauncher.Launch(desktopFile),
    ["--launch-desktop-action", var desktopFile, var actionId] => DesktopEntryLauncher.LaunchAction(desktopFile, actionId),
    ["--lock"] => LockScreenApplication.Run(),
    ["--lock", var backgroundToken] => LockScreenApplication.Run(backgroundToken),
    _ => ShellApplication.Run(),
};

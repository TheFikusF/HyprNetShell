using HyprNetShell.Application;
using HyprNetShell.Application.DesktopEntry;
using HyprNetShell.Application.LockScreen;

return args switch {
    ["--launch-desktop-entry", var desktopFile] => DesktopEntryLauncher.Launch(desktopFile),
    ["--launch-desktop-action", var desktopFile, var actionId] => DesktopEntryLauncher.LaunchAction(desktopFile, actionId),
    ["--launch-uri", var uri] => DesktopEntryLauncher.LaunchUri(uri, privateWindow: false),
    ["--launch-private-uri", var uri] => DesktopEntryLauncher.LaunchUri(uri, privateWindow: true),
    ["--lock"] => LockScreenApplication.Run(),
    ["--lock", var backgroundToken] => LockScreenApplication.Run(backgroundToken),
    _ => ShellApplication.Run(),
};

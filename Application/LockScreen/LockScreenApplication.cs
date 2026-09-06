using System.Diagnostics;
using HyprNetShell.Core.Bar;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.LockScreen;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering;

namespace HyprNetShell.Application.LockScreen;

internal static class LockScreenApplication
{
    private const string PamPolicyPath = "/etc/pam.d/hyprnetshell";

    internal static int Run(string? backgroundToken = null)
    {
        AppLogger.Initialize();
        try
        {
            return RunLockScreen(backgroundToken);
        }
        catch (Exception exception)
        {
            AppLogger.Error("LockScreen", "Fatal lock screen error", exception);
            return 1;
        }
        finally
        {
            AppLogger.Shutdown();
        }
    }

    internal static void Start(HyprLayer layer, StatusBarServices services)
    {
        if (!File.Exists(PamPolicyPath))
        {
            services.ShowShellNotification(
                "Lock screen unavailable",
                $"Install the PAM policy at {PamPolicyPath} first.",
                "lock");
            return;
        }

        LockScreenBackground.Transfer? transfer = null;
        try
        {
            transfer = LockScreenBackground.CaptureAndServe(layer);
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath))
            {
                throw new InvalidOperationException("Could not determine the HyprNetShell executable path.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var entryPath = Environment.GetCommandLineArgs()[0];
            if (entryPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.ArgumentList.Add(entryPath);
            }
            startInfo.ArgumentList.Add("--lock");
            startInfo.ArgumentList.Add(transfer.Token);

            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Could not start the HyprNetShell lock process.");
            transfer = null;
        }
        catch (Exception exception)
        {
            AppLogger.Error("LockScreen", "Could not start the lock screen", exception);
            services.ShowShellNotification("Lock screen failed", exception.Message, "lock");
        }
        finally
        {
            transfer?.Dispose();
        }
    }

    private static int RunLockScreen(string? backgroundToken)
    {
        if (!File.Exists(PamPolicyPath))
        {
            throw new InvalidOperationException(
                $"The lock screen PAM policy is not installed. See Native/pam/README.md and install it as {PamPolicyPath}.");
        }

        var backgrounds = LockScreenBackground.Receive(backgroundToken);
        using var sessionLock = new SessionLock("hyprnetshell");
        if (!sessionLock.MakeCurrent(0))
        {
            throw new InvalidOperationException("Failed to make the lock fallback EGL surface current.");
        }

        using var renderer = new Renderer((int)HyprLayer.TARGET_FRAMERATE, HyprLayer.GetProcAddress);
        var view = new LockScreenView(Theme.Default);
        var opaqueBackground = Theme.Default.Panel with { A = 1 };

        while (sessionLock.Update())
        {
            if (TryUnlock(sessionLock, out var returnCode))
            {
                return returnCode;
            }

            var status = GetStatus(sessionLock.AuthenticationState);
            foreach (var surface in sessionLock.Surfaces)
            {
                backgrounds.TryGetValue(surface.Name, out var background);
                if (!sessionLock.MakeCurrent(surface.Id))
                {
                    continue;
                }

                renderer.BeginFrame(surface.Width, surface.Height, opaqueBackground);
                Layout.Input = LayoutInput.None;
                Layout.BeginInputRegionFrame(surface.Id);
                using (var layout = new Layout(renderer, surface.Width, surface.Height))
                {
                    layout.AddNode(view.Build(
                        surface.Width,
                        surface.Height,
                        sessionLock.PasswordLength,
                        status,
                        background));
                }
                Layout.DrawLayers();
                renderer.EndFrame();
                if (!sessionLock.SwapBuffers(surface.Id))
                {
                    break;
                }
            }

            Thread.Sleep(TimeSpan.FromSeconds(1.0 / HyprLayer.TARGET_FRAMERATE));
        }

        return sessionLock.State == SessionLockState.Unlocked ? 0 : 1;
    }

    private static bool TryUnlock(SessionLock sessionLock, out int returnCode)
    {
        if (sessionLock.AuthenticationState == SessionLockAuthenticationState.Success &&
            sessionLock.State == SessionLockState.Locked)
        {
            returnCode = sessionLock.Unlock() ? 0 : 1;
            return true;
        }

        returnCode = default;
        return false;
    }

    private static LockScreenStatus GetStatus(SessionLockAuthenticationState authenticationState) =>
        authenticationState switch
        {
            SessionLockAuthenticationState.Pending or SessionLockAuthenticationState.Success =>
                LockScreenStatus.Authenticating,
            SessionLockAuthenticationState.Denied => LockScreenStatus.Denied,
            SessionLockAuthenticationState.Error => LockScreenStatus.Error,
            _ => LockScreenStatus.Ready,
        };
}

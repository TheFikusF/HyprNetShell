using System.Runtime.InteropServices;

namespace HyprNetShell;

internal static partial class DesktopEntryLauncher
{
    public static int Launch(string desktopFile)
    {
        var appInfo = Load(desktopFile);
        if (appInfo == IntPtr.Zero)
        {
            return 1;
        }

        try
        {
            if (Gio.g_app_info_launch(appInfo, IntPtr.Zero, IntPtr.Zero, out var error) != 0)
            {
                return 0;
            }

            Console.Error.WriteLine(ErrorMessage(error, $"Could not launch desktop entry {desktopFile}"));
            return 1;
        }
        finally
        {
            Gio.g_object_unref(appInfo);
        }
    }

    public static int LaunchAction(string desktopFile, string actionId)
    {
        var appInfo = Load(desktopFile);
        if (appInfo == IntPtr.Zero)
        {
            return 1;
        }

        try
        {
            Gio.g_desktop_app_info_launch_action(appInfo, actionId, IntPtr.Zero);
            return 0;
        }
        finally
        {
            Gio.g_object_unref(appInfo);
        }
    }

    private static IntPtr Load(string desktopFile)
    {
        var appInfo = Gio.g_desktop_app_info_new_from_filename(desktopFile);
        if (appInfo == IntPtr.Zero)
        {
            Console.Error.WriteLine($"Could not load desktop entry {desktopFile}");
        }

        return appInfo;
    }

    private static string ErrorMessage(IntPtr error, string fallback)
    {
        if (error == IntPtr.Zero)
        {
            return fallback;
        }

        try
        {
            var message = Marshal.PtrToStructure<GError>(error).Message;
            return message == IntPtr.Zero ? fallback : Marshal.PtrToStringUTF8(message) ?? fallback;
        }
        finally
        {
            Gio.g_error_free(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct GError
    {
        private readonly uint Domain;
        private readonly int Code;
        public readonly IntPtr Message;
    }

    private static partial class Gio
    {
        [LibraryImport("gio-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr g_desktop_app_info_new_from_filename(
            string filename);

        [LibraryImport("gio-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void g_desktop_app_info_launch_action(
            IntPtr appInfo,
            string actionName,
            IntPtr launchContext);

        [LibraryImport("gio-2.0")]
        internal static partial int g_app_info_launch(
            IntPtr appInfo,
            IntPtr files,
            IntPtr launchContext,
            out IntPtr error);

        [LibraryImport("gobject-2.0")]
        internal static partial void g_object_unref(IntPtr instance);

        [LibraryImport("glib-2.0")]
        internal static partial void g_error_free(IntPtr error);
    }
}

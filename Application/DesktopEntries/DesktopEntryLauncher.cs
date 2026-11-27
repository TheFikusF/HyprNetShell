using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HyprNetShell.Application.DesktopEntry;

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
            if (LaunchAndWait(appInfo, out var failure))
            {
                return 0;
            }

            Console.Error.WriteLine($"Could not launch desktop entry {desktopFile}: {failure}");
            if (Gio.g_desktop_app_info_get_boolean(appInfo, "DBusActivatable") == 0)
            {
                return 1;
            }

            Console.Error.WriteLine($"Retrying desktop entry {desktopFile} using its Exec command");
            return LaunchExec(desktopFile);
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

    private sealed class LaunchResult
    {
        public bool Completed;
        public bool Success;
        public string? Failure;
    }

    public static int LaunchUri(string url, bool privateWindow)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile ||
            (privateWindow && uri.Scheme is not ("http" or "https")))
        {
            return 1;
        }

        var app = Gio.g_app_info_get_default_for_uri_scheme(uri.Scheme);
        if (app == IntPtr.Zero)
        {
            return 1;
        }

        var value = Marshal.StringToCoTaskMemUTF8(uri.AbsoluteUri);
        var uris = Gio.g_list_prepend(IntPtr.Zero, value);
        try
        {
            if (!privateWindow)
            {
                return LaunchAndWait(app, out _, uris) ? 0 : 1;
            }

            var action = FindPrivateAction(app) ?? FindInstalledPrivateAction(app, uri.Scheme);
            return action is { } target ? LaunchExec(target.File, target.Action, uris) : 2;
        }
        finally
        {
            Gio.g_list_free(uris);
            Marshal.FreeCoTaskMem(value);
            Gio.g_object_unref(app);
        }
    }

    private static (string File, string Action)? FindPrivateAction(IntPtr app)
    {
        if (Gio.g_type_check_instance_is_a(app, Gio.g_desktop_app_info_get_type()) == 0 ||
            Marshal.PtrToStringUTF8(Gio.g_desktop_app_info_get_filename(app)) is not { } desktopFile)
        {
            return null;
        }

        var actions = Gio.g_desktop_app_info_list_actions(app);
        for (var index = 0; actions != IntPtr.Zero; index++)
        {
            var action = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(actions, index * IntPtr.Size));
            if (action is null)
            {
                break;
            }

            if (action.Contains("private", StringComparison.OrdinalIgnoreCase) ||
                action.Contains("incognito", StringComparison.OrdinalIgnoreCase))
            {
                return (desktopFile, action);
            }
        }

        return null;
    }

    private static (string File, string Action)? FindInstalledPrivateAction(IntPtr app, string scheme)
    {
        // Generated default-browser entries can omit actions from the installed entry.
        var id = Marshal.PtrToStringUTF8(Gio.g_app_info_get_id(app));
        if (id?.StartsWith("userapp-", StringComparison.Ordinal) != true || ResolveExecutable(app) is not { } executable)
        {
            return null;
        }

        var name = Marshal.PtrToStringUTF8(Gio.g_app_info_get_name(app));
        var candidates = Gio.g_app_info_get_all_for_type($"x-scheme-handler/{scheme}");
        try
        {
            for (var node = candidates; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                var candidate = Marshal.ReadIntPtr(node);
                if (candidate != app && Marshal.PtrToStringUTF8(Gio.g_app_info_get_name(candidate)) == name &&
                    FindPrivateAction(candidate) is { } action && ResolveExecutable(candidate) == executable)
                {
                    return action;
                }
            }

            return null;
        }
        finally
        {
            for (var node = candidates; node != IntPtr.Zero; node = Marshal.ReadIntPtr(node, IntPtr.Size))
            {
                Gio.g_object_unref(Marshal.ReadIntPtr(node));
            }

            Gio.g_list_free(candidates);
        }
    }

    private static string? ResolveExecutable(IntPtr app)
    {
        var executable = Marshal.PtrToStringUTF8(Gio.g_app_info_get_executable(app));
        if (string.IsNullOrEmpty(executable))
        {
            return null;
        }

        var path = Gio.g_find_program_in_path(executable);
        if (path == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var filename = Path.GetFullPath(Marshal.PtrToStringUTF8(path)!);
            return File.ResolveLinkTarget(filename, returnFinalTarget: true)?.FullName ?? filename;
        }
        finally
        {
            Gio.g_free(path);
        }
    }

    private static unsafe bool LaunchAndWait(IntPtr appInfo, out string? failure, IntPtr uris = default)
    {
        var result = new LaunchResult();
        var handle = GCHandle.Alloc(result);
        var cancellable = Gio.g_cancellable_new();
        var context = Gio.g_main_context_new();
        Gio.g_main_context_push_thread_default(context);
        try
        {
            // The synchronous API only queues D-Bus activation and an async flush.
            // Keep a main context alive until GIO reports the activation reply/flush.
            Gio.g_app_info_launch_uris_async(appInfo, uris, IntPtr.Zero, cancellable,
                &LaunchCompleted, GCHandle.ToIntPtr(handle));
            var timer = Stopwatch.StartNew();
            var cancelled = false;
            while (!result.Completed)
            {
                Gio.g_main_context_iteration(context, 0);
                if (!cancelled && timer.Elapsed >= TimeSpan.FromSeconds(10))
                {
                    cancelled = true;
                    Gio.g_cancellable_cancel(cancellable);
                }
                if (!result.Completed)
                {
                    Thread.Sleep(1);
                }
            }

            failure = cancelled ? $"Launch timed out after 10 seconds: {result.Failure}" : result.Failure;
            return result.Success;
        }
        finally
        {
            Gio.g_main_context_pop_thread_default(context);
            Gio.g_main_context_unref(context);
            Gio.g_object_unref(cancellable);
            handle.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static void LaunchCompleted(IntPtr appInfo, IntPtr asyncResult, IntPtr userData)
    {
        var result = (LaunchResult)GCHandle.FromIntPtr(userData).Target!;
        result.Success = Gio.g_app_info_launch_uris_finish(appInfo, asyncResult, out var error) != 0;
        result.Failure = result.Success ? null : ErrorMessage(error, "GIO launch failed without an error message");
        result.Completed = true;
    }

    private static int LaunchExec(string desktopFile, string? action = null, IntPtr uris = default)
    {
        var keyFile = Gio.g_key_file_new();
        try
        {
            if (Gio.g_key_file_load_from_file(keyFile, desktopFile, 0, out var error) == 0)
            {
                Console.Error.WriteLine($"Could not load Exec fallback for {desktopFile}: {ErrorMessage(error, "Unknown key-file error")}");
                return 1;
            }

            Gio.g_key_file_set_boolean(keyFile, "Desktop Entry", "DBusActivatable", 0);
            var execGroup = action is null ? "Desktop Entry" : $"Desktop Action {action}";
            var execPointer = Gio.g_key_file_get_string(keyFile, execGroup, "Exec", out error);
            if (execPointer == IntPtr.Zero)
            {
                Console.Error.WriteLine($"No Exec fallback for {desktopFile}: {ErrorMessage(error, "Missing Exec command")}");
                return 1;
            }
            try
            {
                var exec = Marshal.PtrToStringUTF8(execPointer)!;
                // Key-file app infos have no filename. Preserve %k before GIO expands
                // the other field codes; skip escaped %% rather than parsing argv.
                var quotedPath = Gio.g_shell_quote(Path.GetFullPath(desktopFile));
                try
                {
                    var command = new StringBuilder();
                    var hasUriField = false;
                    for (var i = 0; i < exec.Length; i++)
                    {
                        if (exec[i] == '%' && i + 1 < exec.Length)
                        {
                            var code = exec[++i];
                            hasUriField |= code is 'u' or 'U' or 'f' or 'F';
                            if (code == 'k')
                            {
                                // Escape literal percent signs for GIO's subsequent expansion.
                                command.Append(Marshal.PtrToStringUTF8(quotedPath)!.Replace("%", "%%"));
                            }
                            else
                            {
                                command.Append('%').Append(code);
                            }
                        }
                        else
                        {
                            command.Append(exec[i]);
                        }
                    }
                    // A desktop action takes no URI argument; give its Exec a field for GIO to expand.
                    if (action is not null && !hasUriField)
                    {
                        command.Append(" %u");
                    }

                    Gio.g_key_file_set_string(keyFile, "Desktop Entry", "Exec", command.ToString());
                }
                finally
                {
                    Gio.g_free(quotedPath);
                }
            }
            finally
            {
                Gio.g_free(execPointer);
            }

            var fallback = Gio.g_desktop_app_info_new_from_keyfile(keyFile);
            if (fallback == IntPtr.Zero)
            {
                Console.Error.WriteLine($"Could not construct Exec fallback for desktop entry {desktopFile}");
                return 1;
            }
            try
            {
                if (LaunchAndWait(fallback, out var failure, uris))
                {
                    return 0;
                }
                if (action is null)
                {
                    Console.Error.WriteLine($"Exec fallback failed for desktop entry {desktopFile}: {failure}");
                }

                return 1;
            }
            finally
            {
                Gio.g_object_unref(fallback);
            }
        }
        finally
        {
            Gio.g_key_file_unref(keyFile);
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
        internal static partial IntPtr g_app_info_get_default_for_uri_scheme(string scheme);

        [LibraryImport("gio-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr g_app_info_get_all_for_type(string contentType);

        [LibraryImport("gio-2.0")]
        internal static partial IntPtr g_app_info_get_id(IntPtr appInfo);

        [LibraryImport("gio-2.0")]
        internal static partial IntPtr g_app_info_get_name(IntPtr appInfo);

        [LibraryImport("gio-2.0")]
        internal static partial IntPtr g_app_info_get_executable(IntPtr appInfo);

        [LibraryImport("glib-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr g_find_program_in_path(string executable);

        [LibraryImport("gio-2.0")]
        internal static partial nuint g_desktop_app_info_get_type();

        [LibraryImport("gobject-2.0")]
        internal static partial int g_type_check_instance_is_a(IntPtr instance, nuint type);

        [LibraryImport("gio-2.0")]
        internal static partial IntPtr g_desktop_app_info_get_filename(IntPtr appInfo);

        [LibraryImport("gio-2.0")]
        internal static partial IntPtr g_desktop_app_info_list_actions(IntPtr appInfo);

        [LibraryImport("glib-2.0")]
        internal static partial IntPtr g_list_prepend(IntPtr list, IntPtr value);

        [LibraryImport("glib-2.0")]
        internal static partial void g_list_free(IntPtr list);
        [LibraryImport("gio-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr g_desktop_app_info_new_from_filename(
            string filename);

        [LibraryImport("gio-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void g_desktop_app_info_launch_action(
            IntPtr appInfo,
            string actionName,
            IntPtr launchContext);

        [LibraryImport("gio-2.0")]
        internal static unsafe partial void g_app_info_launch_uris_async(IntPtr appInfo, IntPtr uris,
            IntPtr launchContext, IntPtr cancellable,
            delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> callback, IntPtr userData);

        [LibraryImport("gio-2.0")]
        internal static partial int g_app_info_launch_uris_finish(IntPtr appInfo, IntPtr result, out IntPtr error);

        [LibraryImport("gio-2.0")]
        internal static partial IntPtr g_cancellable_new();

        [LibraryImport("gio-2.0")]
        internal static partial void g_cancellable_cancel(IntPtr cancellable);

        [LibraryImport("gio-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int g_desktop_app_info_get_boolean(IntPtr appInfo, string key);

        [LibraryImport("gio-2.0")]
        internal static partial IntPtr g_desktop_app_info_new_from_keyfile(IntPtr keyFile);

        [LibraryImport("glib-2.0")]
        internal static partial IntPtr g_main_context_new();

        [LibraryImport("glib-2.0")]
        internal static partial void g_main_context_push_thread_default(IntPtr context);

        [LibraryImport("glib-2.0")]
        internal static partial void g_main_context_pop_thread_default(IntPtr context);

        [LibraryImport("glib-2.0")]
        internal static partial int g_main_context_iteration(IntPtr context, int mayBlock);

        [LibraryImport("glib-2.0")]
        internal static partial void g_main_context_unref(IntPtr context);

        [LibraryImport("glib-2.0")]
        internal static partial IntPtr g_key_file_new();

        [LibraryImport("glib-2.0")]
        internal static partial void g_key_file_unref(IntPtr keyFile);

        [LibraryImport("glib-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial int g_key_file_load_from_file(IntPtr keyFile, string filename, int flags, out IntPtr error);

        [LibraryImport("glib-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void g_key_file_set_boolean(IntPtr keyFile, string group, string key, int value);

        [LibraryImport("glib-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr g_key_file_get_string(IntPtr keyFile, string group, string key, out IntPtr error);

        [LibraryImport("glib-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial void g_key_file_set_string(IntPtr keyFile, string group, string key, string value);

        [LibraryImport("glib-2.0", StringMarshalling = StringMarshalling.Utf8)]
        internal static partial IntPtr g_shell_quote(string value);

        [LibraryImport("glib-2.0")]
        internal static partial void g_free(IntPtr value);

        [LibraryImport("gobject-2.0")]
        internal static partial void g_object_unref(IntPtr instance);

        [LibraryImport("glib-2.0")]
        internal static partial void g_error_free(IntPtr error);
    }
}

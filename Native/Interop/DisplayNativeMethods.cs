using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HyprNetShell.Native.Interop;

internal static partial class DisplayNativeMethods
{
    private const string LIBRARY = "hypr_layer";

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr hypr_temperature_create();

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void hypr_temperature_destroy(IntPtr handle);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_temperature_poll(IntPtr handle);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_temperature_set_temperature(IntPtr handle, int kelvin);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_temperature_is_available(IntPtr handle);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_temperature_is_active(IntPtr handle);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr hypr_temperature_get_error(IntPtr handle);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr hypr_wallpaper_create();

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void hypr_wallpaper_destroy(IntPtr handle);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_wallpaper_poll(IntPtr handle);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int hypr_wallpaper_set_image(IntPtr handle, in byte rgba, int width, int height, int stride);

    [LibraryImport(LIBRARY)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial IntPtr hypr_wallpaper_get_error(IntPtr handle);
}

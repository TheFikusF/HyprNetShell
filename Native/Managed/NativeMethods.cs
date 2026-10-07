using System.Runtime.InteropServices;

namespace HyprNetShell;

internal static partial class NativeMethods
{
    private const string HYPR_LAYER_LIBRARY = "hypr_layer";

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial IntPtr hypr_layer_create(int reservedHeight);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial void hypr_layer_destroy(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_poll_events(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_should_close(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_has_error(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial ulong hypr_layer_get_topology_serial(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_bar_count(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial ulong hypr_layer_get_bar_id(IntPtr layer, int index);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_bar_width(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_bar_height(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_output_name(IntPtr layer, ulong outputId, [Out] byte[] buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_make_current(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_swap_buffers(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_set_input_regions(
        IntPtr layer,
        ulong outputId,
        int[] rectangles,
        int rectangleCount);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_set_keyboard_interactive_bar(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_set_screenshot_overlay(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_make_screenshot_current(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_swap_screenshot_buffers(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_capture_output(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_capture_width(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_capture_height(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_capture_stride(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_copy_capture(IntPtr layer, [Out] byte[] buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial ulong hypr_layer_get_window_serial(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_window_count(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial ulong hypr_layer_get_window_id(IntPtr layer, int index);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_window_title(IntPtr layer, ulong id, [Out] byte[]? buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_window_app_id(IntPtr layer, ulong id, [Out] byte[]? buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_window_identifier(IntPtr layer, ulong id, [Out] byte[]? buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_window_address(IntPtr layer, ulong id, [Out] byte[]? buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_thumbnail_error(IntPtr layer, ulong id, [Out] byte[]? buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_thumbnails_available(IntPtr layer);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_set_thumbnails_enabled(IntPtr layer, int enabled);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_set_window_thumbnail_visible(IntPtr layer, ulong id, int visible);

    // Full-window, aspect-preserved thumbnails <=640x360, tightly packed RGBA8,
    // top-left origin, transform normalized, straight alpha (transparent RGB is zero).
    // Native storage is reused; copy each revision into a fresh managed frame buffer.
    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_get_window_thumbnail_info(IntPtr layer, ulong id,
        out ulong revision, out int width, out int height, out int stride);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_copy_window_thumbnail(IntPtr layer, ulong id,
        ulong expectedRevision, [Out] byte[] buffer, int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int hypr_layer_set_clipboard(
        IntPtr layer,
        byte[] data,
        int dataLength,
        string mimeType);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial double hypr_layer_get_pointer_x(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial double hypr_layer_get_pointer_y(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_pointer_inside(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_pointer_button(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial double hypr_layer_take_scroll(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_take_key(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_take_key_control(IntPtr layer, ulong outputId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_layer_take_text(
        IntPtr layer,
        ulong outputId,
        [Out] byte[] buffer,
        int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr hypr_layer_get_proc_address(string name);

    [LibraryImport(HYPR_LAYER_LIBRARY, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr hypr_lock_create(string pamService);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial void hypr_lock_destroy(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_poll_events(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_get_state(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_has_error(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial ulong hypr_lock_get_topology_serial(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_get_surface_count(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial ulong hypr_lock_get_surface_id(IntPtr sessionLock, int index);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_get_surface_width(IntPtr sessionLock, ulong surfaceId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_get_surface_height(IntPtr sessionLock, ulong surfaceId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_get_surface_name(
        IntPtr sessionLock,
        ulong surfaceId,
        [Out] byte[] buffer,
        int bufferSize);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_make_current(IntPtr sessionLock, ulong surfaceId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_swap_buffers(IntPtr sessionLock, ulong surfaceId);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_get_password_length(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_get_auth_state(IntPtr sessionLock);

    [LibraryImport(HYPR_LAYER_LIBRARY)]
    internal static partial int hypr_lock_unlock(IntPtr sessionLock);
}

using System.Runtime.InteropServices;

namespace HyprNetShell;

internal static partial class NativeMethods
{
    private const string HyprLayerLibrary = "hypr_layer";

    [LibraryImport(HyprLayerLibrary)]
    internal static partial IntPtr hypr_layer_create(int reservedHeight);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial void hypr_layer_destroy(IntPtr layer);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_poll_events(IntPtr layer);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_should_close(IntPtr layer);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_has_error(IntPtr layer);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial ulong hypr_layer_get_topology_serial(IntPtr layer);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_get_bar_count(IntPtr layer);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial ulong hypr_layer_get_bar_id(IntPtr layer, int index);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_get_bar_width(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_get_bar_height(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_get_output_name(IntPtr layer, ulong outputId, [Out] byte[] buffer, int bufferSize);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_make_current(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_swap_buffers(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_set_input_regions(
        IntPtr layer,
        ulong outputId,
        int[] rectangles,
        int rectangleCount);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_set_keyboard_interactive_bar(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial double hypr_layer_get_pointer_x(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial double hypr_layer_get_pointer_y(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_pointer_inside(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_pointer_button(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial double hypr_layer_take_scroll(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_take_key(IntPtr layer, ulong outputId);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_take_text(
        IntPtr layer,
        ulong outputId,
        [Out] byte[] buffer,
        int bufferSize);

    [LibraryImport(HyprLayerLibrary, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr hypr_layer_get_proc_address(string name);
}

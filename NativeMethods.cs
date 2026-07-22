using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell;

public sealed class HyprLayer : IDisposable
{
    private const float TARGET_FRAMERATE = 60.0f;

    private static readonly TimeSpan TargetFrameDuration = TimeSpan.FromSeconds(1.0 / TARGET_FRAMERATE);

    private IDisposable? _sigIntRegistration;
    private IDisposable? _sigTermRegistration;
    private IntPtr _window;
    private long _frameStartTimestamp;
    private int _shutdownRequested;
    private int _returnCode;
    private bool _lastPointerDown;
    private bool _keyboardInteractive;

    public (int width, int height) LayerSize { get; private set; }
    public LayoutInput Input { get; private set; } = LayoutInput.None;
    public int PressedKey { get; private set; } = -1;
    public string TextInput { get; private set; } = "";
    public int ReturnCode => _window == IntPtr.Zero ? _returnCode : NativeMethods.hypr_layer_has_error(_window);

    public HyprLayer(int reservedHeight)
    {
        try
        {
            _sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, TerminationHandler);
            _sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, TerminationHandler);

            _window = NativeMethods.hypr_layer_create_top_bar(reservedHeight);
            if (_window == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Failed to create the Wayland layer-shell bar. See native error output above.");
            }

            NativeMethods.hypr_layer_make_current(_window);
            LayerSize = (NativeMethods.hypr_layer_get_width(_window), NativeMethods.hypr_layer_get_height(_window));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void TerminationHandler(PosixSignalContext context)
    {
        context.Cancel = true;
        Volatile.Write(ref _shutdownRequested, 1);
    }

    public static IntPtr GetProcAddress(string name) => NativeMethods.hypr_layer_get_proc_address(name);

    public bool Update()
    {
        _frameStartTimestamp = Stopwatch.GetTimestamp();
        if (_window == IntPtr.Zero ||
            Volatile.Read(ref _shutdownRequested) != 0 ||
            NativeMethods.hypr_layer_should_close(_window) != 0)
        {
            return false;
        }

        NativeMethods.hypr_layer_poll_events(_window);
        if (NativeMethods.hypr_layer_should_close(_window) != 0)
        {
            return false;
        }

        NativeMethods.hypr_layer_make_current(_window);

        LayerSize = (NativeMethods.hypr_layer_get_width(_window), NativeMethods.hypr_layer_get_height(_window));
        var hasPointer = NativeMethods.hypr_layer_pointer_inside(_window) != 0;
        var pointerDown = hasPointer && NativeMethods.hypr_layer_pointer_button_down(_window) != 0;
        Input = hasPointer
            ? new LayoutInput(
                (float)NativeMethods.hypr_layer_get_pointer_x(_window),
                (float)NativeMethods.hypr_layer_get_pointer_y(_window),
                pointerDown,
                pointerDown && !_lastPointerDown,
                true,
                (float)NativeMethods.hypr_layer_take_scroll(_window))
            : LayoutInput.None with { ScrollDelta = (float)NativeMethods.hypr_layer_take_scroll(_window) };
        PressedKey = NativeMethods.hypr_layer_take_key(_window);
        var textBuffer = new byte[128];
        var textLength = NativeMethods.hypr_layer_take_text(_window, textBuffer, textBuffer.Length);
        TextInput = textLength > 0 ? Encoding.UTF8.GetString(textBuffer, 0, textLength) : "";
        _lastPointerDown = pointerDown;
        return true;
    }

    public void Swap()
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.hypr_layer_swap_buffers(_window);
        var elapsed = Stopwatch.GetElapsedTime(_frameStartTimestamp);
        var remaining = TargetFrameDuration - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            Thread.Sleep(remaining);
        }
    }

    public void SetInputRegions(IReadOnlyList<Rect> regions)
    {
        if (_window == IntPtr.Zero)
        {
            return;
        }

        if (regions.Count == 0)
        {
            NativeMethods.hypr_layer_set_input_regions(_window, [], 0);
            return;
        }

        var rectangles = new int[regions.Count * 4];
        for (var i = 0; i < regions.Count; i++)
        {
            var region = regions[i];
            var x = (int)MathF.Floor(region.X);
            var y = (int)MathF.Floor(region.Y);
            var right = (int)MathF.Ceiling(region.X + region.Width);
            var bottom = (int)MathF.Ceiling(region.Y + region.Height);
            rectangles[i * 4] = x;
            rectangles[i * 4 + 1] = y;
            rectangles[i * 4 + 2] = Math.Max(0, right - x);
            rectangles[i * 4 + 3] = Math.Max(0, bottom - y);
        }

        NativeMethods.hypr_layer_set_input_regions(_window, rectangles, regions.Count);
    }

    public void SetKeyboardInteractivity(bool enabled)
    {
        if (_window != IntPtr.Zero && _keyboardInteractive != enabled)
        {
            NativeMethods.hypr_layer_set_keyboard_interactivity(_window, enabled ? 1 : 0);
            _keyboardInteractive = enabled;
        }
    }

    public void Dispose()
    {
        if (_window != IntPtr.Zero)
        {
            _returnCode = NativeMethods.hypr_layer_has_error(_window);
            NativeMethods.hypr_layer_destroy(_window);
            _window = IntPtr.Zero;
        }

        _sigTermRegistration?.Dispose();
        _sigTermRegistration = null;
        _sigIntRegistration?.Dispose();
        _sigIntRegistration = null;
    }
}

internal static partial class NativeMethods
{
    private const string HyprLayerLibrary = "hypr_layer";

    [LibraryImport(HyprLayerLibrary)]
    internal static partial IntPtr hypr_layer_create_top_bar(int reservedHeight);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial void hypr_layer_destroy(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial void hypr_layer_make_current(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial void hypr_layer_swap_buffers(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial void hypr_layer_poll_events(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial void hypr_layer_set_input_regions(IntPtr window, int[] rectangles, int rectangleCount);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial void hypr_layer_set_keyboard_interactivity(IntPtr window, int enabled);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_get_width(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_get_height(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial double hypr_layer_get_pointer_x(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial double hypr_layer_get_pointer_y(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_pointer_inside(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_pointer_button_down(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_take_key(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_take_text(IntPtr window, [Out] byte[] buffer, int bufferSize);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial double hypr_layer_take_scroll(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_should_close(IntPtr window);

    [LibraryImport(HyprLayerLibrary)]
    internal static partial int hypr_layer_has_error(IntPtr window);

    [LibraryImport(HyprLayerLibrary, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr hypr_layer_get_proc_address(string name);
}

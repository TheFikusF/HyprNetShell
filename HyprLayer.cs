using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell;

public sealed class HyprLayer : IDisposable
{
    private const float TARGET_FRAMERATE = 30.0f;
    private const int OUTPUT_NAME_BUFFER_SIZE = 256;

    private static readonly TimeSpan TargetFrameDuration = TimeSpan.FromSeconds(1.0 / TARGET_FRAMERATE);

    private readonly Dictionary<ulong, Output> _outputsById = [];
    private readonly List<Output> _outputs = [];
    private readonly ReadOnlyCollection<Output> _readOnlyOutputs;
    private PosixSignalRegistration? _sigIntRegistration;
    private PosixSignalRegistration? _sigTermRegistration;
    private IntPtr _layer;
    private long _frameStartTimestamp;
    private int _shutdownRequested;
    private int _returnCode;
    private ulong _topologySerial;
    private ulong _keyboardInteractiveBar = ulong.MaxValue;
    private bool _hasTopologySerial;

    public IReadOnlyList<Output> Outputs => _readOnlyOutputs;
    public bool TopologyChanged { get; private set; }
    public int ReturnCode => _layer == IntPtr.Zero ? _returnCode : NativeMethods.hypr_layer_has_error(_layer);

    public HyprLayer(int reservedHeight)
    {
        _readOnlyOutputs = _outputs.AsReadOnly();

        try
        {
            _sigIntRegistration = PosixSignalRegistration.Create(PosixSignal.SIGINT, TerminationHandler);
            _sigTermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, TerminationHandler);

            _layer = NativeMethods.hypr_layer_create(reservedHeight);
            if (_layer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Failed to create the Wayland layer-shell bars. See native error output above.");
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public sealed class Output
    {
        private bool _lastPointerDown;

        internal Output(ulong id)
        {
            Id = id;
        }

        public ulong Id { get; }
        public string Name { get; internal set; } = "";
        public int Width { get; internal set; }
        public int Height { get; internal set; }
        public LayoutInput Input { get; internal set; } = LayoutInput.None;
        public int PressedKey { get; internal set; } = -1;
        public string TextInput { get; internal set; } = "";

        internal void UpdateInput(IntPtr layer)
        {
            var hasPointer = NativeMethods.hypr_layer_pointer_inside(layer, Id) != 0;
            var pointerDown = hasPointer && NativeMethods.hypr_layer_pointer_button(layer, Id) != 0;
            var scrollDelta = (float)NativeMethods.hypr_layer_take_scroll(layer, Id);

            Input = hasPointer
                ? new LayoutInput(
                    (float)NativeMethods.hypr_layer_get_pointer_x(layer, Id),
                    (float)NativeMethods.hypr_layer_get_pointer_y(layer, Id),
                    pointerDown,
                    pointerDown && !_lastPointerDown,
                    true,
                    scrollDelta)
                : LayoutInput.None with { ScrollDelta = scrollDelta };
            PressedKey = NativeMethods.hypr_layer_take_key(layer, Id);

            var textBuffer = new byte[128];
            var textLength = NativeMethods.hypr_layer_take_text(layer, Id, textBuffer, textBuffer.Length);
            TextInput = textLength > 0
                ? Encoding.UTF8.GetString(textBuffer, 0, Math.Min(textLength, textBuffer.Length))
                : "";
            _lastPointerDown = pointerDown;
        }
    }

    public static IntPtr GetProcAddress(string name) => NativeMethods.hypr_layer_get_proc_address(name);

    public bool Update()
    {
        _frameStartTimestamp = Stopwatch.GetTimestamp();
        if (_layer == IntPtr.Zero ||
            Volatile.Read(ref _shutdownRequested) != 0 ||
            NativeMethods.hypr_layer_should_close(_layer) != 0)
        {
            return false;
        }

        NativeMethods.hypr_layer_poll_events(_layer);
        if (NativeMethods.hypr_layer_should_close(_layer) != 0)
        {
            return false;
        }

        ReconcileOutputs();
        foreach (var output in _outputs)
        {
            output.UpdateInput(_layer);
        }

        return true;
    }

    public bool MakeCurrent(ulong outputId) =>
        _layer != IntPtr.Zero && NativeMethods.hypr_layer_make_current(_layer, outputId) != 0;

    public bool SwapBuffers(ulong outputId) =>
        _layer != IntPtr.Zero && NativeMethods.hypr_layer_swap_buffers(_layer, outputId) != 0;

    public void SetInputRegions(ulong outputId, IReadOnlyList<Rect> regions)
    {
        if (_layer == IntPtr.Zero)
        {
            return;
        }

        if (regions.Count == 0)
        {
            NativeMethods.hypr_layer_set_input_regions(_layer, outputId, [], 0);
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

        NativeMethods.hypr_layer_set_input_regions(_layer, outputId, rectangles, regions.Count);
    }

    public void SetKeyboardInteractiveBar(ulong outputId)
    {
        if (_layer != IntPtr.Zero && _keyboardInteractiveBar != outputId)
        {
            NativeMethods.hypr_layer_set_keyboard_interactive_bar(_layer, outputId);
            _keyboardInteractiveBar = outputId;
        }
    }

    public void PaceFrame()
    {
        var elapsed = Stopwatch.GetElapsedTime(_frameStartTimestamp);
        var remaining = TargetFrameDuration - elapsed;
        if (remaining > TimeSpan.Zero)
        {
            Thread.Sleep(remaining);
        }
    }

    public void Dispose()
    {
        if (_layer != IntPtr.Zero)
        {
            _returnCode = NativeMethods.hypr_layer_has_error(_layer);
            NativeMethods.hypr_layer_destroy(_layer);
            _layer = IntPtr.Zero;
        }

        _outputs.Clear();
        _outputsById.Clear();

        _sigTermRegistration?.Dispose();
        _sigTermRegistration = null;
        _sigIntRegistration?.Dispose();
        _sigIntRegistration = null;
    }

    private void TerminationHandler(PosixSignalContext context)
    {
        context.Cancel = true;
        Volatile.Write(ref _shutdownRequested, 1);
    }

    private void ReconcileOutputs()
    {
        var topologySerial = NativeMethods.hypr_layer_get_topology_serial(_layer);
        TopologyChanged = !_hasTopologySerial || topologySerial != _topologySerial;
        if (!TopologyChanged)
        {
            return;
        }

        _hasTopologySerial = true;
        _topologySerial = topologySerial;

        var currentIds = new HashSet<ulong>();
        var reconciled = new List<Output>();
        var barCount = Math.Max(0, NativeMethods.hypr_layer_get_bar_count(_layer));
        for (var index = 0; index < barCount; index++)
        {
            var id = NativeMethods.hypr_layer_get_bar_id(_layer, index);
            if (!currentIds.Add(id))
            {
                continue;
            }

            if (!_outputsById.TryGetValue(id, out var output))
            {
                output = new Output(id);
                _outputsById.Add(id, output);
            }

            output.Name = GetOutputName(id);
            output.Width = NativeMethods.hypr_layer_get_bar_width(_layer, id);
            output.Height = NativeMethods.hypr_layer_get_bar_height(_layer, id);
            reconciled.Add(output);
        }

        foreach (var id in _outputsById.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            _outputsById.Remove(id);
        }

        _outputs.Clear();
        _outputs.AddRange(reconciled);
    }

    private string GetOutputName(ulong outputId)
    {
        var buffer = new byte[OUTPUT_NAME_BUFFER_SIZE];
        var length = NativeMethods.hypr_layer_get_output_name(_layer, outputId, buffer, buffer.Length);
        if (length <= 0)
        {
            return "";
        }

        if (length >= buffer.Length)
        {
            buffer = new byte[length + 1];
            length = NativeMethods.hypr_layer_get_output_name(_layer, outputId, buffer, buffer.Length);
            if (length <= 0)
            {
                return "";
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, Math.Min(length, buffer.Length)).TrimEnd('\0');
    }
}

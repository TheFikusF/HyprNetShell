using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using HyprNetShell.GUI.Layout;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Native.Managed;

public sealed class HyprLayer : IDisposable
{
    private sealed class ClipboardRequest(byte[] data, CancellationToken token)
    {
        public byte[] Data { get; } = data;
        public CancellationToken Token { get; } = token;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }

    public const float TARGET_FRAMERATE = 60.0f;
    private const int OUTPUT_NAME_BUFFER_SIZE = 256;
    private const int KEYBOARD_TEXT_BUFFER_SIZE = 128;

    private static readonly TimeSpan TargetFrameDuration = TimeSpan.FromSeconds(1.0 / TARGET_FRAMERATE);

    private readonly Lock _clipboardLock = new();
    private readonly Queue<ClipboardRequest> _clipboardRequests = new();
    private readonly Dictionary<ulong, Output> _outputsById = [];
    private readonly List<Output> _outputs = [];
    private readonly ReadOnlyCollection<Output> _readOnlyOutputs;
    private readonly byte[] _keyboardTextBytes = new byte[KEYBOARD_TEXT_BUFFER_SIZE];
    private readonly char[] _sensitiveTextChars = new char[KEYBOARD_TEXT_BUFFER_SIZE];
    private int _sensitiveTextLength;
    private bool _disposed;
    private PosixSignalRegistration? _sigIntRegistration;
    private PosixSignalRegistration? _sigTermRegistration;
    private IntPtr _layer;
    private long _frameStartTimestamp;
    private int _shutdownRequested;
    private int _returnCode;
    private ulong _topologySerial;
    private bool _hasTopologySerial;

    internal ReadOnlySpan<char> SensitiveTextInput => _sensitiveTextChars.AsSpan(0, _sensitiveTextLength);
    internal IntPtr NativeHandle => _layer;
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

    public sealed record CapturedImage(int Width, int Height, int Stride, byte[] BgraPixels);

    public sealed class Output
    {
        private bool _lastPointerDown;

        internal Output(ulong id)
        {
            Id = id;
        }

        public ulong Id
        {
            get;
        }
        public string Name { get; internal set; } = "";
        public int Width
        {
            get; internal set;
        }
        public int Height
        {
            get; internal set;
        }
        public LayoutInput Input { get; internal set; } = LayoutInput.None;
        public int PressedKey { get; internal set; } = -1;
        public bool ControlPressed
        {
            get; internal set;
        }
        public string TextInput { get; internal set; } = "";

        internal int UpdateInput(IntPtr layer, byte[] textBuffer, Span<char> sensitiveText)
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
                : LayoutInput.None with {
                    ScrollDelta = scrollDelta
                };
            PressedKey = NativeMethods.hypr_layer_take_key(layer, Id);
            ControlPressed = NativeMethods.hypr_layer_take_key_control(layer, Id) != 0;

            _lastPointerDown = pointerDown;
            TextInput = "";
            try
            {
                var textLength = NativeMethods.hypr_layer_take_text(layer, Id, textBuffer, textBuffer.Length);
                if (textLength <= 0)
                {
                    return 0;
                }

                var textBytes = textBuffer.AsSpan(0, Math.Min(textLength, textBuffer.Length));
                if (!sensitiveText.IsEmpty)
                {
                    return Encoding.UTF8.GetChars(textBytes, sensitiveText);
                }

                TextInput = Encoding.UTF8.GetString(textBytes);
                return 0;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(textBuffer);
            }
        }
    }

    public static IntPtr GetProcAddress(string name) => NativeMethods.hypr_layer_get_proc_address(name);

    public bool Update() => Update(null);

    internal void ClearSensitiveTextInput()
    {
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_sensitiveTextChars.AsSpan()));
        _sensitiveTextLength = 0;
    }

    internal bool Update(ulong? sensitiveOwnerId)
    {
        ClearSensitiveTextInput();
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

        ProcessClipboardRequests();
        ReconcileOutputs();
        foreach (var output in _outputs)
        {
            if (output.Id == sensitiveOwnerId)
            {
                _sensitiveTextLength = output.UpdateInput(_layer, _keyboardTextBytes, _sensitiveTextChars);
            }
            else
            {
                output.UpdateInput(_layer, _keyboardTextBytes, Span<char>.Empty);
            }
        }

        return true;
    }

    public bool MakeCurrent(ulong outputId) =>
        _layer != IntPtr.Zero && NativeMethods.hypr_layer_make_current(_layer, outputId) != 0;

    public bool SwapBuffers(ulong outputId) =>
        _layer != IntPtr.Zero && NativeMethods.hypr_layer_swap_buffers(_layer, outputId) != 0;

    public bool MakeScreenshotCurrent(ulong outputId) =>
        _layer != IntPtr.Zero && NativeMethods.hypr_layer_make_screenshot_current(_layer, outputId) != 0;

    public bool SwapScreenshotBuffers(ulong outputId) =>
        _layer != IntPtr.Zero && NativeMethods.hypr_layer_swap_screenshot_buffers(_layer, outputId) != 0;

    public CapturedImage? CaptureOutput(ulong outputId)
    {
        if (_layer == IntPtr.Zero || NativeMethods.hypr_layer_capture_output(_layer, outputId) == 0)
        {
            return null;
        }

        var width = NativeMethods.hypr_layer_get_capture_width(_layer);
        var height = NativeMethods.hypr_layer_get_capture_height(_layer);
        var stride = NativeMethods.hypr_layer_get_capture_stride(_layer);
        if (width <= 0 || height <= 0 || stride < width * 4 || (long)stride * height > int.MaxValue)
        {
            return null;
        }

        var pixels = new byte[stride * height];
        return NativeMethods.hypr_layer_copy_capture(_layer, pixels, pixels.Length) == pixels.Length
            ? new CapturedImage(width, height, stride, pixels)
            : null;
    }

    public bool SetClipboard(byte[] data, string mimeType) =>
        _layer != IntPtr.Zero && data.Length > 0 &&
        NativeMethods.hypr_layer_set_clipboard(_layer, data, data.Length, mimeType) != 0;

    // Copies caller-owned data. The caller remains responsible for clearing its original buffer.
    public Task<bool> SetSensitiveClipboardAsync(byte[] data, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(data), "Sensitive clipboard data must contain 1..4096 bytes.");
        }

        lock (_clipboardLock)
        {
            if (token.IsCancellationRequested)
            {
                return Task.FromCanceled<bool>(token);
            }

            if (_disposed || Volatile.Read(ref _shutdownRequested) != 0)
            {
                return Task.FromResult(false);
            }

            var request = new ClipboardRequest((byte[])data.Clone(), token);
            _clipboardRequests.Enqueue(request);
            request.Registration = token.Register(() =>
            {
                lock (_clipboardLock)
                {
                    if (!request.Completion.Task.IsCompleted)
                    {
                        CryptographicOperations.ZeroMemory(request.Data);
                        request.Completion.TrySetCanceled(token);
                    }
                }
            });
            return request.Completion.Task;
        }
    }

    private void ProcessClipboardRequests()
    {
        lock (_clipboardLock)
        {
            while (_clipboardRequests.TryDequeue(out var request))
            {
                try
                {
                    if (request.Completion.Task.IsCompleted)
                    {
                        continue;
                    }

                    if (request.Token.IsCancellationRequested)
                    {
                        request.Completion.TrySetCanceled(request.Token);
                    }
                    else
                    {
                        request.Completion.TrySetResult(_layer != IntPtr.Zero &&
                            NativeMethods.hypr_layer_set_sensitive_clipboard(_layer, request.Data, request.Data.Length) != 0);
                    }
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(request.Data);
                    request.Registration.Unregister();
                }
            }
        }
    }

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

    public void SetOverviewOverlay(ulong outputId)
    {
        if (_layer != IntPtr.Zero)
        {
            NativeMethods.hypr_layer_set_overview_overlay(_layer, outputId);
        }
    }

    public void SetScreenshotOverlay(ulong outputId)
    {
        if (_layer != IntPtr.Zero)
        {
            NativeMethods.hypr_layer_set_screenshot_overlay(_layer, outputId);
        }
    }

    public void SetKeyboardInteractiveBar(ulong outputId)
    {
        if (_layer != IntPtr.Zero)
        {
            NativeMethods.hypr_layer_set_keyboard_interactive_bar(_layer, outputId);
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
        ClearSensitiveTextInput();
        lock (_clipboardLock)
        {
            _disposed = true;
            while (_clipboardRequests.TryDequeue(out var request))
            {
                CryptographicOperations.ZeroMemory(request.Data);
                request.Completion.TrySetResult(false);
                request.Registration.Unregister();
            }

            if (_layer != IntPtr.Zero)
            {
                _returnCode = NativeMethods.hypr_layer_has_error(_layer);
                NativeMethods.hypr_layer_destroy(_layer);
                _layer = IntPtr.Zero;
            }
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

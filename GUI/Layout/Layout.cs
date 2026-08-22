using System.Diagnostics;
using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout;

public class Layout : IDisposable
{
    internal static IRenderApi Renderer { get; private set; } = null!;
    public static LayoutInput Input { get; set; } = LayoutInput.None;
    private static readonly List<Rect> InputRegions = [];
    private static bool _diagnosticsEnabled;
    private static long _layoutsCreated;
    private static long _layoutsDrawn;
    private static long _boxDraws;
    private static long _widthMeasurements;
    private static long _heightMeasurements;
    private static long _childArrayAllocations;
    private static long _childArrayElements;
    private readonly BoxNode _root;
    
    public Layout(IRenderApi renderer, int width, int height, Style? style = null, LayoutInput? input = null)
    {
        Renderer = renderer;
        if (_diagnosticsEnabled)
        {
            _layoutsCreated++;
        }

        if (input.HasValue)
        {
            Input = input.Value;
        }

        _root = new BoxNode(width, height)
        {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = style ?? new Style()
        };
    }

    public void AddNode(Node node)
    {
        _root.AddNode(node);
    }
    
    public void Dispose()
    {
        if (_diagnosticsEnabled)
        {
            _layoutsDrawn++;
        }

        _root.Draw(Renderer, 0, 0);
    }

    [Conditional(PerformanceProfiling.Symbol)]
    public static void BeginDiagnosticsFrame(bool enabled)
    {
        _diagnosticsEnabled = enabled;
        _layoutsCreated = 0;
        _layoutsDrawn = 0;
        _boxDraws = 0;
        _widthMeasurements = 0;
        _heightMeasurements = 0;
        _childArrayAllocations = 0;
        _childArrayElements = 0;
    }

    public static LayoutFrameMetrics GetFrameMetrics() => new(
        _layoutsCreated,
        _layoutsDrawn,
        _boxDraws,
        _widthMeasurements,
        _heightMeasurements,
        _childArrayAllocations,
        _childArrayElements);

    [Conditional(PerformanceProfiling.Symbol)]
    internal static void RecordBoxDraw()
    {
        if (_diagnosticsEnabled)
        {
            _boxDraws++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    internal static void RecordWidthMeasurement()
    {
        if (_diagnosticsEnabled)
        {
            _widthMeasurements++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    internal static void RecordHeightMeasurement()
    {
        if (_diagnosticsEnabled)
        {
            _heightMeasurements++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    internal static void RecordChildArray(int elements)
    {
        if (_diagnosticsEnabled)
        {
            _childArrayAllocations++;
            _childArrayElements += elements;
        }
    }

    public static void BeginInputRegionFrame()
    {
        InputRegions.Clear();
    }

    public static IReadOnlyList<Rect> GetInputRegions() => InputRegions;

    internal static void AddInputRegion(Rect rect)
    {
        if (rect.Width > 0 && rect.Height > 0)
        {
            InputRegions.Add(rect);
        }
    }
}

public readonly record struct LayoutInput(
    float PointerX,
    float PointerY,
    bool PointerDown,
    bool PointerPressed = false,
    bool HasPointer = true,
    float ScrollDelta = 0)
{
    public static LayoutInput None { get; } = new(0, 0, false, false, false);

    internal bool Contains(Rect rect) => HasPointer && rect.Contains(PointerX, PointerY);
}

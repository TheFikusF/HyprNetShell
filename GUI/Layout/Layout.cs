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
    private static readonly RenderLayer[] RenderLayerOrder = Enum.GetValues<RenderLayer>().Order().ToArray();
    private static readonly Dictionary<RenderLayer, List<Action<IRenderApi>>> LayerDraws = [];
    private static readonly Dictionary<ulong, OutputLayerInputRegions> LayerInputRegionsByOutput = [];
    private static OutputLayerInputRegions? _currentLayerInputRegions;

    private sealed class OutputLayerInputRegions
    {
        public readonly Dictionary<RenderLayer, List<Rect>> Active = [];
        public readonly Dictionary<RenderLayer, List<Rect>> Current = [];
    }

    private static ulong _currentOutputId;
    private static RenderLayer _drawingLayer;
    private static bool _diagnosticsEnabled;
    private static long _layoutsCreated;
    private static long _layoutsDrawn;
    private static long _boxDraws;
    private static long _widthMeasurements;
    private static long _heightMeasurements;
    private static long _childArrayAllocations;
    private static long _childArrayElements;
    private readonly BoxNode _root;
    private readonly RenderLayer _layer;

    public Layout(
        IRenderApi renderer,
        int width,
        int height,
        Style style = default,
        LayoutInput? input = null,
        RenderLayer layer = RenderLayer.Bar)
    {
        Renderer = renderer;
        _layer = layer;
        if (_diagnosticsEnabled)
        {
            _layoutsCreated++;
        }

        if (input.HasValue)
        {
            Input = input.Value;
        }

        _root = new BoxNode(width, height) {
            Direction = Direction.Horizontal,
            HorizontalAlignment = ItemsAlignment.Spread,
            VerticalAlignment = ItemsAlignment.Center,
            Style = style
        };
    }

    private static void ClearLayerDraws()
    {
        foreach (var draws in LayerDraws.Values)
        {
            draws.Clear();
        }
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

        DrawOnLayer(_layer, renderer => _root.Draw(renderer, 0, 0));
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

    public static void BeginInputRegionFrame(ulong outputId)
    {
        InputRegions.Clear();
        ClearLayerDraws();
        _currentOutputId = outputId;
        if (!LayerInputRegionsByOutput.TryGetValue(outputId, out var outputRegions))
        {
            outputRegions = new OutputLayerInputRegions();
            LayerInputRegionsByOutput.Add(outputId, outputRegions);
        }
        _currentLayerInputRegions = outputRegions;

        foreach (var regions in outputRegions.Active.Values)
        {
            regions.Clear();
        }

        foreach (var (layer, regions) in outputRegions.Current)
        {
            if (!outputRegions.Active.TryGetValue(layer, out var activeRegions))
            {
                activeRegions = [];
                outputRegions.Active.Add(layer, activeRegions);
            }
            // Blocking retains the previous snapshot plus current registrations;
            // unregistering must affect only the next frame's snapshot.
            activeRegions.AddRange(regions);
            regions.Clear();
        }
        _drawingLayer = RenderLayer.Bar;
    }

    public static void RemoveOutput(ulong outputId)
    {
        LayerInputRegionsByOutput.Remove(outputId);
        if (_currentOutputId == outputId)
        {
            _currentLayerInputRegions = null;
            InputRegions.Clear();
            ClearLayerDraws();
        }
    }

    public static void DrawLayers()
    {
        foreach (var layer in RenderLayerOrder)
        {
            if (!LayerDraws.TryGetValue(layer, out var draws))
            {
                continue;
            }

            _drawingLayer = layer;
            for (var index = 0; index < draws.Count; index++)
            {
                draws[index](Renderer);
            }
        }

        ClearLayerDraws();
    }

    public static void DrawOnLayer(RenderLayer layer, Action<IRenderApi> draw)
    {
        if (!LayerDraws.TryGetValue(layer, out var draws))
        {
            draws = [];
            LayerDraws.Add(layer, draws);
        }

        draws.Add(draw);
    }

    internal static void RegisterLayerInputRegion(RenderLayer layer, Rect rect)
    {
        var outputRegions = _currentLayerInputRegions
            ?? throw new InvalidOperationException("BeginInputRegionFrame must be called before registering input regions.");
        if (!outputRegions.Active.TryGetValue(layer, out var activeRegions))
        {
            activeRegions = [];
            outputRegions.Active.Add(layer, activeRegions);
        }

        if (!activeRegions.Contains(rect))
        {
            activeRegions.Add(rect);
        }

        var nextRegionsByLayer = outputRegions.Current;
        if (!nextRegionsByLayer.TryGetValue(layer, out var nextRegions))
        {
            nextRegions = [];
            nextRegionsByLayer.Add(layer, nextRegions);
        }

        if (!nextRegions.Contains(rect))
        {
            nextRegions.Add(rect);
        }
    }

    internal static void UnregisterNextLayerInputRegion(RenderLayer layer, Rect rect)
    {
        var outputRegions = _currentLayerInputRegions
            ?? throw new InvalidOperationException("BeginInputRegionFrame must be called before unregistering input regions.");
        if (outputRegions.Current.TryGetValue(layer, out var regions))
        {
            regions.Remove(rect);
        }
    }

    internal static bool IsLowerLayerClickBlocked =>
        _currentLayerInputRegions is not null &&
        _currentLayerInputRegions.Active.Any(pair => pair.Key > _drawingLayer && pair.Value.Any(Input.Contains));

    public static IReadOnlyList<Rect> GetInputRegions() => InputRegions;

    internal static void AddInputRegion(Rect rect)
    {
        if (rect.Width > 0 && rect.Height > 0)
        {
            InputRegions.Add(rect);
            if (_drawingLayer != RenderLayer.OptionsSelector)
            {
                RegisterLayerInputRegion(_drawingLayer, rect);
            }
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

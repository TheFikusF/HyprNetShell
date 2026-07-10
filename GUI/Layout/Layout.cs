using HyprNetShell.GUI.Layout.Nodes;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout;

public class Layout : IDisposable
{
    internal static IRenderApi Renderer { get; private set; } = null!;
    public static LayoutInput Input { get; set; } = LayoutInput.None;
    private static readonly List<Rect> InputRegions = [];
    private readonly BoxNode _root;
    
    public Layout(IRenderApi renderer, int width, int height, Style? style = null, LayoutInput? input = null)
    {
        Renderer = renderer;
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
        _root.Draw(Renderer, 0, 0);
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
    bool HasPointer = true)
{
    public static LayoutInput None { get; } = new(0, 0, false, false, false);

    internal bool Contains(Rect rect) => HasPointer && rect.Contains(PointerX, PointerY);
}

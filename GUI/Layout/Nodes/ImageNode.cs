using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.GUI.Layout.Nodes;

public class ImageNode : Node
{
    public override int Width { get; }
    public override int Height { get; }
    private readonly string? _imagePath;
    private readonly RawImageData? _rawImage;
    private readonly EncodedImageData? _encodedImage;
    private readonly SvgAsset? _svgAsset;
    private readonly Color _color;
    private readonly bool _loadAsync;

    public float RotationRadians { get; init; }

    public ImageNode(
        string imagePath,
        int width,
        int height,
        Color? multiplicativeColor = null,
        bool loadAsync = false)
    {
        _imagePath = imagePath;
        _color = multiplicativeColor ?? Color.White;
        _loadAsync = loadAsync;
        Width = width;
        Height = height;
    }

    public ImageNode(SvgAsset svgAsset, int width, int height, Color color)
    {
        _svgAsset = svgAsset;
        _color = color;
        Width = width;
        Height = height;
    }

    public ImageNode(RawImageData image, int width, int height, Color? multiplicativeColor = null)
    {
        _rawImage = image;
        _color = multiplicativeColor ?? Color.White;
        Width = width;
        Height = height;
    }

    public ImageNode(EncodedImageData image, int width, int height, Color? multiplicativeColor = null)
    {
        _encodedImage = image;
        _color = multiplicativeColor ?? Color.White;
        Width = width;
        Height = height;
    }

    public override void Draw(IRenderApi renderer, int x, int y)
    {
        UpdateInteractionState(x, y);
        Layout.AddInputRegion(new Rect(x, y, Width, Height));
        var rect = new Rect(x, y, Width, Height);
        if (_svgAsset is not null)
        {
            renderer.DrawImage(_svgAsset, rect, _color.PushOpacity(Opacity), RotationRadians);
        }
        else if (_rawImage is not null)
        {
            renderer.DrawImage(_rawImage, rect, _color.PushOpacity(Opacity), RotationRadians);
        }
        else if (_encodedImage is not null)
        {
            renderer.DrawImage(_encodedImage, rect, _color.PushOpacity(Opacity), RotationRadians);
        }
        else if (_imagePath is not null)
        {
            renderer.DrawImage(_imagePath, rect, _color.PushOpacity(Opacity), _loadAsync, RotationRadians);
        }
    }
}

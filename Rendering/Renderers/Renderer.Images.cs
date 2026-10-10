using System.Numerics;

using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Rendering.ShaderModels;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering.Renderers;

public sealed unsafe partial class Renderer
{
    private readonly TextureGeometryBatch _textureGeometry;
    private readonly TextureShader _textureProgramShader;
    private readonly SvgTextureShader _svgTextureProgramShader;
    private readonly ImageShadowShader _imageShadowProgramShader;
    private readonly TextureRepository _textureRepository;

    private uint _batchTexture;
    private ITextureBatchShader? _batchTextureShader;

    public void DrawImage(
        string imagePath,
        Rect rect,
        Color multiplicativeColor,
        bool loadAsync = false,
        float rotationRadians = 0)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        // Path lookups can delete replaced or idle textures referenced by a pending batch.
        FlushPendingGeometry();
        var texture = _textureRepository.GetTexture(
            imagePath,
            Math.Max(1, (int)MathF.Ceiling(rect.Width * 2)),
            Math.Max(1, (int)MathF.Ceiling(rect.Height * 2)),
            loadAsync);
        if (texture is null)
        {
            return;
        }

        DrawTexture(texture.Value, rect, multiplicativeColor, _textureProgramShader, rotationRadians);
    }

    public void DrawRoundedImage(RawImageData image, Rect rect, float radius, Color color)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        FlushPendingGeometry();
        _textureProgramShader.Bind();
        _textureProgramShader.CornerRadius = Math.Clamp(radius, 0, Math.Min(rect.Width, rect.Height) / 2);
        _textureProgramShader.ImageSize = new Vector2(rect.Width, rect.Height);
        DrawImage(image, rect, color);
        // Rounded-image uniforms must not affect subsequent unrounded texture batches.
        FlushTextureGeometry();
        _textureProgramShader.CornerRadius = 0f;
    }

    public void DrawImage(RawImageData image, Rect rect, Color multiplicativeColor, float rotationRadians = 0)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        // Submit earlier draws only when an upload could change their texture contents.
        if (_textureRepository.NeedsUpload(image))
        {
            FlushPendingGeometry();
        }

        var texture = _textureRepository.GetTexture(image);
        DrawTexture(texture, rect, multiplicativeColor, _textureProgramShader, rotationRadians);
    }

    public void DrawImage(
        EncodedImageData image,
        Rect rect,
        Color multiplicativeColor,
        float rotationRadians = 0)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var texture = _textureRepository.GetTexture(image);
        if (texture is not null)
        {
            DrawTexture(texture.Value, rect, multiplicativeColor, _textureProgramShader, rotationRadians);
        }
    }

    public void DrawImageShadow(
        string imagePath, Rect rect, Color shadowColor, float blurRadius,
        float offsetX = 0, float offsetY = 2, bool loadAsync = false)
    {
        if (!CanDrawImageShadow(rect, shadowColor, blurRadius, offsetX, offsetY)
            || string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        FlushPendingGeometry();
        var texture = _textureRepository.GetTexture(imagePath,
            Math.Max(1, (int)MathF.Ceiling(rect.Width * 2)),
            Math.Max(1, (int)MathF.Ceiling(rect.Height * 2)), loadAsync);
        if (texture is not null)
        {
            DrawImageShadowTexture(texture.Value, rect, shadowColor, blurRadius, offsetX, offsetY);
        }
    }

    public void DrawImageShadow(
        SvgAsset asset, Rect rect, Color shadowColor, float blurRadius,
        float offsetX = 0, float offsetY = 2)
    {
        if (!CanDrawImageShadow(rect, shadowColor, blurRadius, offsetX, offsetY))
        {
            return;
        }

        var texture = _textureRepository.GetTexture(asset);
        if (texture is not null)
        {
            DrawImageShadowTexture(texture.Value, rect, shadowColor, blurRadius, offsetX, offsetY);
        }
    }

    private static bool CanDrawImageShadow(
        Rect rect, Color color, float blurRadius, float offsetX, float offsetY) =>
        float.IsFinite(rect.X) && float.IsFinite(rect.Y)
        && float.IsFinite(rect.Width) && float.IsFinite(rect.Height)
        && rect.Width > 0 && rect.Height > 0 && color.A > 0
        && float.IsFinite(blurRadius) && blurRadius >= 0
        && float.IsFinite(offsetX) && float.IsFinite(offsetY);

    private void DrawImageShadowTexture(
        Texture texture, Rect rect, Color color, float blurRadius, float offsetX, float offsetY)
    {
        FlushPendingGeometry();
        // One extra screen pixel includes the source texture's bilinear border.
        var padding = blurRadius + 1;
        var paddedRect = new Rect(rect.X + offsetX - padding, rect.Y + offsetY - padding,
            rect.Width + 2 * padding, rect.Height + 2 * padding);
        _imageShadowProgramShader.Bind();
        _imageShadowProgramShader.BlurStep = new Vector2(
            blurRadius / (4 * rect.Width), blurRadius / (4 * rect.Height));
        RecordShadow();
        DrawTexture(texture, paddedRect, color, _imageShadowProgramShader,
            uvPaddingX: padding / rect.Width, uvPaddingY: padding / rect.Height);
        // Blur uniforms belong to this draw, not a later texture batch.
        FlushTextureGeometry();
    }

    private void DrawTexture(
        Texture texture,
        Rect rect,
        Color color,
        ITextureBatchShader shader,
        float rotationRadians = 0,
        float uvPaddingX = 0,
        float uvPaddingY = 0)
    {
        RecordTextureDraw();
        FlushColoredGeometry();
        FlushRoundedGeometry();
        if (_textureGeometry.HasPendingGeometry && (_batchTexture != texture.Id || _batchTextureShader != shader))
        {
            FlushTextureGeometry();
        }

        _batchTexture = texture.Id;
        _batchTextureShader = shader;

        var x = rect.X;
        var y = rect.Y;
        var width = rect.Width;
        var height = rect.Height;

        var topLeft = RotatePoint(x, y, rect, rotationRadians);
        var topRight = RotatePoint(x + width, y, rect, rotationRadians);
        var bottomRight = RotatePoint(x + width, y + height, rect, rotationRadians);
        var bottomLeft = RotatePoint(x, y + height, rect, rotationRadians);

        ReadOnlySpan<float> vertices =
        [
            topLeft.X, topLeft.Y, -uvPaddingX, -uvPaddingY, color.R, color.G, color.B, color.A,
            topRight.X, topRight.Y, 1 + uvPaddingX, -uvPaddingY, color.R, color.G, color.B, color.A,
            bottomRight.X, bottomRight.Y, 1 + uvPaddingX, 1 + uvPaddingY, color.R, color.G, color.B, color.A,
            topLeft.X, topLeft.Y, -uvPaddingX, -uvPaddingY, color.R, color.G, color.B, color.A,
            bottomRight.X, bottomRight.Y, 1 + uvPaddingX, 1 + uvPaddingY, color.R, color.G, color.B, color.A,
            bottomLeft.X, bottomLeft.Y, -uvPaddingX, 1 + uvPaddingY, color.R, color.G, color.B, color.A,
        ];
        _textureGeometry.Append(vertices);
    }

    private void FlushTextureGeometry()
    {
        if (!_textureGeometry.HasPendingGeometry)
        {
            return;
        }

        _batchTextureShader!.PrepareTextureBatch(new Vector2(Width, Height));

        // Uploads and font measurement can change bindings while this batch is pending.
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _batchTexture);
        var bytes = _textureGeometry.Upload();
        RecordBufferUpload(bytes);
        _textureGeometry.Draw();
    }

    private static (float X, float Y) RotatePoint(float x, float y, Rect rect, float radians)
    {
        if (radians == 0)
        {
            return (x, y);
        }

        var centerX = rect.X + rect.Width * 0.5f;
        var centerY = rect.Y + rect.Height * 0.5f;
        var offsetX = x - centerX;
        var offsetY = y - centerY;
        var cosine = MathF.Cos(radians);
        var sine = MathF.Sin(radians);
        return (
            centerX + offsetX * cosine - offsetY * sine,
            centerY + offsetX * sine + offsetY * cosine);
    }

    public void DrawImage(
        SvgAsset asset,
        Rect rect,
        Color? color,
        float rotationRadians = 0,
        float opacity = 1)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var texture = _textureRepository.GetTexture(asset);
        if (texture is null)
        {
            return;
        }

        var renderedColor = (color ?? Color.White).PushOpacity(opacity);
        if (color is null)
        {
            DrawTexture(texture.Value, rect, renderedColor, _textureProgramShader, rotationRadians);
        }

        else
        {
            DrawTexture(texture.Value, rect, renderedColor, _svgTextureProgramShader, rotationRadians);
        }
    }
}

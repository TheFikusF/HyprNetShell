using HyprNetShell.Rendering.Primitives;
using ImageMagick;
using SkiaSharp;
using Silk.NET.OpenGL;
using Svg.Skia;

namespace HyprNetShell.Rendering;

public sealed unsafe class Renderer : IRenderApi, IDisposable
{
    private readonly GL _gl;
    private readonly uint _program;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly int _viewportLocation;
    private readonly uint _textureProgram;
    private readonly uint _textureVao;
    private readonly uint _textureVbo;
    private readonly int _textureViewportLocation;
    private readonly int _textureLocation;
    private readonly uint _svgTextureProgram;
    private readonly int _svgTextureViewportLocation;
    private readonly int _svgTextureLocation;
    private readonly int _svgTextureColorLocation;
    private readonly Dictionary<string, ImageTexture> _textures = new(StringComparer.Ordinal);
    private readonly Dictionary<SvgAsset, uint> _svgTextures = [];
    private readonly FontRenderer _font;
    private bool _disposed;
    private int _frameWidth;
    private int _frameHeight;

    public int Width => _frameWidth;
    public int Height => _frameHeight;

    public static event Action OnFrameStart;
    public static event Action OnFrameEnd;

    public Renderer(Func<string, IntPtr> getProcAddress)
    {
        _gl = GL.GetApi(getProcAddress);
        _program = GlShaders.CreateProgram(_gl, GlShaders.COLORED_VERTEX, GlShaders.COLORED_FRAGMENT, "colored");
        _viewportLocation = _gl.GetUniformLocation(_program, "uViewport");
        _textureProgram = GlShaders.CreateProgram(_gl, GlShaders.TEXTURED_VERTEX, GlShaders.TEXTURE_FRAGMENT, "texture");
        _textureViewportLocation = _gl.GetUniformLocation(_textureProgram, "uViewport");
        _textureLocation = _gl.GetUniformLocation(_textureProgram, "uTexture");
        _svgTextureProgram = GlShaders.CreateProgram(
            _gl, GlShaders.TEXTURED_VERTEX, GlShaders.SVG_TEXTURE_FRAGMENT, "SVG texture");
        _svgTextureViewportLocation = _gl.GetUniformLocation(_svgTextureProgram, "uViewport");
        _svgTextureLocation = _gl.GetUniformLocation(_svgTextureProgram, "uTexture");
        _svgTextureColorLocation = _gl.GetUniformLocation(_svgTextureProgram, "uColor");

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(6 * 6 * sizeof(float)), null, BufferUsageARB.DynamicDraw);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 6 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, 6 * sizeof(float),
            (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _textureVao = _gl.GenVertexArray();
        _textureVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_textureVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textureVbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(6 * 4 * sizeof(float)), null, BufferUsageARB.DynamicDraw);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _font = new FontRenderer(_gl);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
    }

    public void BeginFrame(int width, int height)
    {
        _frameWidth = Math.Max(width, 1);
        _frameHeight = Math.Max(height, 1);

        _gl.Viewport(0, 0, (uint)_frameWidth, (uint)_frameHeight);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.UseProgram(_program);
        _gl.Uniform2(_viewportLocation, (float)_frameWidth, (float)_frameHeight);
        _font.SetViewport(_frameWidth, _frameHeight);
        
        OnFrameStart?.Invoke();
    }

    public void EndFrame()
    {
        OnFrameEnd?.Invoke();
        _gl.Flush();
    }

    public float MeasureText(string text, float fontSize) => _font.MeasureText(text, fontSize);

    public void FillRect(Rect rect, Color color) => DrawRect(rect.X, rect.Y, rect.Width, rect.Height, color);

    public void FillRoundedRect(Rect rect, float radius, Color color)
        => FillRoundedRect(rect, new BorderRadius(radius), color);

    public void FillRoundedRect(Rect rect, BorderRadius radius, Color color)
        => DrawRoundedRect(rect.X, rect.Y, rect.Width, rect.Height, radius, color);

    public void FillRoundedRectHorizontalGradient(Rect rect, BorderRadius radius, Color left, Color right, float offset)
        => DrawRoundedGradient(rect, radius, left, right, offset);

    public void StrokeRect(Rect rect, float thickness, Color color)
        => DrawBorder(rect.X, rect.Y, rect.Width, rect.Height, thickness, color);

    private void DrawRect(float x, float y, float width, float height, Color color)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        Span<float> vertices =
        [
            x, y, color.R, color.G, color.B, color.A,
            x + width, y, color.R, color.G, color.B, color.A,
            x + width, y + height, color.R, color.G, color.B, color.A,
            x, y, color.R, color.G, color.B, color.A,
            x + width, y + height, color.R, color.G, color.B, color.A,
            x, y + height, color.R, color.G, color.B, color.A,
        ];

        DrawVertices(vertices, PrimitiveType.Triangles);
    }

    private void DrawRoundedRect(float x, float y, float width, float height, BorderRadius radius, Color color)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        radius = ClampCornerRadius(radius, width, height);
        var points = new List<(float X, float Y)>();
        AddCorner(points, x + width - radius.TopRight, y + radius.TopRight, radius.TopRight, -90.0f, 0.0f, x + width, y);
        AddCorner(points, x + width - radius.BottomRight, y + height - radius.BottomRight, radius.BottomRight, 0.0f, 90.0f, x + width, y + height);
        AddCorner(points, x + radius.BottomLeft, y + height - radius.BottomLeft, radius.BottomLeft, 90.0f, 180.0f, x, y + height);
        AddCorner(points, x + radius.TopLeft, y + radius.TopLeft, radius.TopLeft, 180.0f, 270.0f, x, y);

        var vertices = new float[(points.Count + 2) * 6];
        WriteVertex(vertices, 0, x + width * 0.5f, y + height * 0.5f, color);
        for (var i = 0; i < points.Count; i++)
        {
            WriteVertex(vertices, i + 1, points[i].X, points[i].Y, color);
        }
        WriteVertex(vertices, points.Count + 1, points[0].X, points[0].Y, color);

        DrawVertices(vertices, PrimitiveType.TriangleFan);
    }

    private void DrawRoundedGradient(Rect rect, BorderRadius radius, Color left, Color right, float offset)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        var strips = Math.Max(16, Math.Min(150, (int)MathF.Ceiling(rect.Width / 1.0f)));
        var stripWidth = rect.Width / strips;
        offset -= MathF.Floor(offset);

        for (var i = 0; i < strips; i++)
        {
            var x0 = rect.X + i * stripWidth;
            var x1 = i == strips - 1 ? rect.X + rect.Width : x0 + stripWidth + 0.75f;
            var centerX = x0 + (x1 - x0) * 0.5f - rect.X;
            var top = rect.Y + RoundedTopInset(centerX, rect.Width, radius);
            var bottom = rect.Y + rect.Height - RoundedBottomInset(centerX, rect.Width, radius);
            var height = bottom - top;
            if (height <= 0)
            {
                continue;
            }

            var t = ((float)i / Math.Max(1, strips - 1) + offset) % 1.0f;
            t = t < 0.5f ? t * 2.0f : (1.0f - t) * 2.0f;
            DrawRect(x0, top, x1 - x0, height, Color.Lerp(left, right, t));
        }
    }

    private static float RoundedTopInset(float x, float width, BorderRadius radius)
    {
        if (x < radius.TopLeft && radius.TopLeft > 0.0f)
        {
            return CircleInset(radius.TopLeft, radius.TopLeft - x);
        }

        if (x > width - radius.TopRight && radius.TopRight > 0.0f)
        {
            return CircleInset(radius.TopRight, x - (width - radius.TopRight));
        }

        return 0.0f;
    }

    private static float RoundedBottomInset(float x, float width, BorderRadius radius)
    {
        if (x < radius.BottomLeft && radius.BottomLeft > 0.0f)
        {
            return CircleInset(radius.BottomLeft, radius.BottomLeft - x);
        }

        if (x > width - radius.BottomRight && radius.BottomRight > 0.0f)
        {
            return CircleInset(radius.BottomRight, x - (width - radius.BottomRight));
        }

        return 0.0f;
    }

    private static float CircleInset(float radius, float dx)
    {
        dx = MathF.Min(MathF.Abs(dx), radius);
        return radius - MathF.Sqrt(MathF.Max(0.0f, radius * radius - dx * dx));
    }

    private void DrawBorder(float x, float y, float width, float height, float thickness, Color color)
    {
        DrawRect(x, y, width, thickness, color);
        DrawRect(x, y + height - thickness, width, thickness, color);
        DrawRect(x, y, thickness, height, color);
        DrawRect(x + width - thickness, y, thickness, height, color);
    }

    public void DrawText(string text, float x, float y, float fontSize, Color color, float charDistance)
    {
        _font.DrawText(text, x, y, fontSize, charDistance, color);
    }

    public void DrawImage(string imagePath, Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || string.IsNullOrWhiteSpace(imagePath))
        {
            return;
        }

        var texture = GetTexture(imagePath);
        if (texture is null)
        {
            return;
        }

        var x = rect.X;
        var y = rect.Y;
        var width = rect.Width;
        var height = rect.Height;
        Span<float> vertices =
        [
            x, y, 0.0f, 0.0f,
            x + width, y, 1.0f, 0.0f,
            x + width, y + height, 1.0f, 1.0f,
            x, y, 0.0f, 0.0f,
            x + width, y + height, 1.0f, 1.0f,
            x, y + height, 0.0f, 1.0f,
        ];

        _gl.UseProgram(_textureProgram);
        _gl.Uniform2(_textureViewportLocation, (float)_frameWidth, (float)_frameHeight);
        _gl.Uniform1(_textureLocation, 0);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture.Value.Id);
        _gl.BindVertexArray(_textureVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textureVbo);
        fixed (float* data = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    public void DrawImage(SvgAsset asset, Rect rect, Color color)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var texture = GetTexture(asset);
        if (texture is null)
        {
            return;
        }

        var x = rect.X;
        var y = rect.Y;
        var width = rect.Width;
        var height = rect.Height;
        Span<float> vertices =
        [
            x, y, 0.0f, 0.0f,
            x + width, y, 1.0f, 0.0f,
            x + width, y + height, 1.0f, 1.0f,
            x, y, 0.0f, 0.0f,
            x + width, y + height, 1.0f, 1.0f,
            x, y + height, 0.0f, 1.0f,
        ];

        _gl.UseProgram(_svgTextureProgram);
        _gl.Uniform2(_svgTextureViewportLocation, (float)_frameWidth, (float)_frameHeight);
        _gl.Uniform1(_svgTextureLocation, 0);
        _gl.Uniform4(_svgTextureColorLocation, color.R, color.G, color.B, color.A);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture.Value);
        _gl.BindVertexArray(_textureVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textureVbo);
        fixed (float* data = vertices)
        {
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                (nuint)(vertices.Length * sizeof(float)),
                data,
                BufferUsageARB.DynamicDraw);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private void DrawVertices(ReadOnlySpan<float> vertices, PrimitiveType primitiveType)
    {
        _gl.UseProgram(_program);
        _gl.Uniform2(_viewportLocation, (float)_frameWidth, (float)_frameHeight);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* data = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }
        _gl.DrawArrays(primitiveType, 0, (uint)(vertices.Length / 6));
    }

    private static BorderRadius ClampCornerRadius(BorderRadius radius, float width, float height)
    {
        radius = new BorderRadius(
            MathF.Max(0.0f, radius.TopLeft),
            MathF.Max(0.0f, radius.TopRight),
            MathF.Max(0.0f, radius.BottomRight),
            MathF.Max(0.0f, radius.BottomLeft));

        var scale = 1.0f;
        scale = ClampRadiusScale(scale, width, radius.TopLeft + radius.TopRight);
        scale = ClampRadiusScale(scale, width, radius.BottomLeft + radius.BottomRight);
        scale = ClampRadiusScale(scale, height, radius.TopLeft + radius.BottomLeft);
        scale = ClampRadiusScale(scale, height, radius.TopRight + radius.BottomRight);

        return scale >= 1.0f
            ? radius
            : new BorderRadius(
                radius.TopLeft * scale,
                radius.TopRight * scale,
                radius.BottomRight * scale,
                radius.BottomLeft * scale);
    }

    private static float ClampRadiusScale(float scale, float available, float used)
    {
        return used <= 0.0f ? scale : MathF.Min(scale, available / used);
    }

    private static void AddCorner(
        List<(float X, float Y)> points,
        float cx,
        float cy,
        float radius,
        float fromDegrees,
        float toDegrees,
        float sharpX,
        float sharpY)
    {
        if (radius <= 0.0f)
        {
            points.Add((sharpX, sharpY));
            return;
        }

        const int SEGMENTS = 6;
        for (var i = 0; i <= SEGMENTS; i++)
        {
            var t = fromDegrees + (toDegrees - fromDegrees) * i / SEGMENTS;
            var radians = t * MathF.PI / 180.0f;
            points.Add((cx + MathF.Cos(radians) * radius, cy + MathF.Sin(radians) * radius));
        }
    }

    private static void WriteVertex(float[] vertices, int vertex, float x, float y, Color color)
    {
        var offset = vertex * 6;
        vertices[offset] = x;
        vertices[offset + 1] = y;
        vertices[offset + 2] = color.R;
        vertices[offset + 3] = color.G;
        vertices[offset + 4] = color.B;
        vertices[offset + 5] = color.A;
    }

    private ImageTexture? GetTexture(string imagePath)
    {
        try
        {
            if (!File.Exists(imagePath))
            {
                return null;
            }

            var path = Path.GetFullPath(imagePath);
            var modified = File.GetLastWriteTimeUtc(path);
            if (_textures.TryGetValue(path, out var cached) && cached.Modified == modified)
            {
                return cached;
            }

            if (cached.Id != 0)
            {
                _gl.DeleteTexture(cached.Id);
            }

            var image = LoadImage(path);
            if (image is null)
            {
                return null;
            }

            var texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            fixed (byte* data = image.Value.Pixels)
            {
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba,
                    (uint)image.Value.Width,
                    (uint)image.Value.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    data);
            }

            var loaded = new ImageTexture(texture, modified);
            _textures[path] = loaded;
            return loaded;
        }
        catch
        {
            return null;
        }
    }

    private uint? GetTexture(SvgAsset asset)
    {
        if (_svgTextures.TryGetValue(asset, out var cached))
        {
            return cached;
        }

        try
        {
            var raster = asset.Rasterize();
            var texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            fixed (byte* data = raster.Pixels.Span)
            {
                _gl.TexImage2D(
                    TextureTarget.Texture2D,
                    0,
                    InternalFormat.Rgba,
                    (uint)raster.Width,
                    (uint)raster.Height,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    data);
            }

            _svgTextures[asset] = texture;
            return texture;
        }
        catch
        {
            return null;
        }
    }

    private static DecodedImage? LoadImage(string path)
    {
        return Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase) ||
               Path.GetExtension(path).Equals(".svgz", StringComparison.OrdinalIgnoreCase)
            ? LoadSvg(path)
            : LoadRasterImage(path);
    }

    private static DecodedImage? LoadSvg(string path)
    {
        try
        {
            using var svg = new SKSvg();
            var picture = svg.Load(path);
            if (picture is null || picture.CullRect.Width <= 0 || picture.CullRect.Height <= 0) return null;

            const float maxDimension = 512.0f;
            var scale = MathF.Min(1.0f, maxDimension / MathF.Max(picture.CullRect.Width, picture.CullRect.Height));
            using var stream = new MemoryStream();
            using var colorSpace = SKColorSpace.CreateSrgb();
            picture.ToImage(
                stream, SKColors.Transparent, SKEncodedImageFormat.Png, 100, scale, scale,
                SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);
            stream.Position = 0;
            using var image = new MagickImage(stream);
            return DecodeMagickImage(image);
        }
        catch
        {
            return null;
        }
    }

    private static DecodedImage? LoadRasterImage(string path)
    {
        try
        {
            using var image = new MagickImage(path);
            return DecodeMagickImage(image);
        }
        catch (MagickException)
        {
            return null;
        }
    }

    private static DecodedImage? DecodeMagickImage(MagickImage image)
    {
        image.Format = MagickFormat.Rgba;
        var pixels = image.ToByteArray();
        return pixels.Length == image.Width * image.Height * 4
            ? new DecodedImage((int)image.Width, (int)image.Height, pixels)
            : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_textureVbo);
        _gl.DeleteVertexArray(_textureVao);
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_textureProgram);
        _gl.DeleteProgram(_svgTextureProgram);
        foreach (var texture in _textures.Values)
        {
            _gl.DeleteTexture(texture.Id);
        }
        foreach (var texture in _svgTextures.Values)
        {
            _gl.DeleteTexture(texture);
        }
        _font.Dispose();
        _disposed = true;
    }

    private readonly record struct ImageTexture(uint Id, DateTime Modified);
    private readonly record struct DecodedImage(int Width, int Height, byte[] Pixels);
}

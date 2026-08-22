using System.Runtime.InteropServices;
using HyprNetShell.Rendering.Primitives;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

public sealed unsafe class Renderer : IRenderApi, IDisposable
{
    private const int MAX_SHADOW_CACHE_ENTRIES = 256;
    private const int MAX_SHADOW_CACHE_BYTES = 64 * 1024 * 1024;
    private const float MAX_SHADOW_DISTANCE = 256.0f;

    private readonly record struct ShadowTextureKey(
        int Width,
        int Height,
        BorderRadius Radius,
        float Distance,
        int Spread);

    private readonly record struct CachedShadow(Texture Texture, long LastAccess, int ByteSize);

    private readonly GL _gl;

    private readonly uint _program;

    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly List<float> _coloredVertices = new(6 * 6);

    private readonly int _viewportLocation;

    private readonly uint _textureProgram;
    private readonly uint _textureVao;
    private readonly uint _textureVbo;
    private readonly int _textureViewportLocation;
    private readonly int _textureLocation;
    private readonly int _textureColorLocation;

    private readonly uint _svgTextureProgram;
    private readonly int _svgTextureViewportLocation;
    private readonly int _svgTextureLocation;
    private readonly int _svgTextureColorLocation;

    private readonly TextureRepository _textureRepository;
    private readonly Dictionary<ShadowTextureKey, CachedShadow> _shadowTextures = [];

    private readonly FontRenderer _font;
    private long _shadowAccessCounter;
    private long _shadowCacheBytes;

    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public static event Action? OnFrameStart;
    public static event Action? OnFrameEnd;

    public Renderer(Func<string, IntPtr> getProcAddress)
    {
        _gl = GL.GetApi(getProcAddress);
        _program = GlShaders.CreateProgram(_gl, GlShaders.COLORED_VERTEX, GlShaders.COLORED_FRAGMENT, "colored");
        _viewportLocation = _gl.GetUniformLocation(_program, "uViewport");
        _textureProgram = GlShaders.CreateProgram(_gl, GlShaders.TEXTURED_VERTEX, GlShaders.TEXTURE_FRAGMENT, "texture");
        _textureViewportLocation = _gl.GetUniformLocation(_textureProgram, "uViewport");
        _textureLocation = _gl.GetUniformLocation(_textureProgram, "uTexture");
        _textureColorLocation = _gl.GetUniformLocation(_textureProgram, "uColor");
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
        _textureRepository = new TextureRepository(_gl);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(
            BlendingFactor.SrcAlpha,
            BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One,
            BlendingFactor.OneMinusSrcAlpha);
    }

    public void BeginFrame(int width, int height)
    {
        _coloredVertices.Clear();
        _textureRepository.RemoveUnusedPathResources();

        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);

        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
        _gl.ClearColor(0.0f, 0.0f, 0.0f, 0.0f);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.UseProgram(_program);
        _gl.Uniform2(_viewportLocation, (float)Width, (float)Height);
        _font.SetViewport(Width, Height);

        OnFrameStart?.Invoke();
    }

    public void EndFrame()
    {
        OnFrameEnd?.Invoke();
        FlushColoredGeometry();
        _gl.Flush();
    }

    public float MeasureText(string text, float fontSize) => _font.MeasureText(text, fontSize);

    public void FillRect(Rect rect, Color color) => DrawRect(rect.X, rect.Y, rect.Width, rect.Height, color);

    public void FillRoundedRect(Rect rect, float radius, Color color)
        => FillRoundedRect(rect, new BorderRadius(radius), color);

    public void FillRoundedRect(Rect rect, BorderRadius radius, Color color)
        => DrawRoundedRect(rect.X, rect.Y, rect.Width, rect.Height, radius, color);

    public void FillRoundedShadow(Rect rect, BorderRadius radius, Color color, float distance)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f || color.A <= 0.0f ||
            !float.IsFinite(distance) || distance <= 0.0f)
        {
            return;
        }

        distance = MathF.Min(distance, MAX_SHADOW_DISTANCE);
        var width = Math.Max(1, (int)MathF.Ceiling(rect.Width));
        var height = Math.Max(1, (int)MathF.Ceiling(rect.Height));
        var spread = Math.Max(1, (int)MathF.Ceiling(distance));
        radius = ClampCornerRadius(radius, width, height);
        var key = new ShadowTextureKey(width, height, radius, distance, spread);

        if (!_shadowTextures.TryGetValue(key, out var cached))
        {
            var byteSize = checked((width + spread * 2) * (height + spread * 2) * 4);
            cached = new CachedShadow(CreateShadowTexture(key), ++_shadowAccessCounter, byteSize);
            CacheShadow(key, cached);
        }
        else
        {
            cached = cached with { LastAccess = ++_shadowAccessCounter };
            _shadowTextures[key] = cached;
        }

        DrawTexture(
            cached.Texture,
            new Rect(rect.X - spread, rect.Y - spread, width + spread * 2, height + spread * 2),
            color,
            _textureProgram,
            _textureViewportLocation,
            _textureLocation,
            _textureColorLocation);
    }

    public void FillRoundedBorder(Rect rect, BorderRadius radius, Insets thickness, Color color)
        => DrawRoundedBorder(rect, radius, thickness, color);

    public void FillRoundedRectGradient(
        Rect rect,
        BorderRadius radius,
        Gradient gradient,
        GradientDirection direction,
        float offset = 0.0f)
        => DrawRoundedGradient(rect, radius, gradient, direction, offset);

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

    private Texture CreateShadowTexture(ShadowTextureKey key)
    {
        var textureWidth = checked(key.Width + key.Spread * 2);
        var textureHeight = checked(key.Height + key.Spread * 2);
        var pixels = new byte[checked(textureWidth * textureHeight * 4)];

        for (var y = 0; y < textureHeight; y++)
        {
            var boxY = y + 0.5f - key.Spread;
            for (var x = 0; x < textureWidth; x++)
            {
                var offset = (y * textureWidth + x) * 4;
                pixels[offset] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;

                var boxX = x + 0.5f - key.Spread;
                var outsideDistance = RoundedRectOutsideDistance(boxX, boxY, key.Width, key.Height, key.Radius);
                if (outsideDistance <= 0.0f || outsideDistance >= key.Distance)
                {
                    continue;
                }

                var gradient = 1.0f - outsideDistance / key.Distance;
                pixels[offset + 3] = (byte)Math.Clamp(
                    (int)MathF.Round(gradient * gradient * 255.0f),
                    0,
                    255);
            }
        }

        var id = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, id);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        fixed (byte* data = pixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba,
                (uint)textureWidth,
                (uint)textureHeight,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                data);
        }

        return new Texture(id);
    }

    private void CacheShadow(ShadowTextureKey key, CachedShadow shadow)
    {
        while (_shadowTextures.Count > 0 &&
               (_shadowTextures.Count >= MAX_SHADOW_CACHE_ENTRIES ||
                _shadowCacheBytes + shadow.ByteSize > MAX_SHADOW_CACHE_BYTES))
        {
            var oldest = _shadowTextures.MinBy(entry => entry.Value.LastAccess);
            _gl.DeleteTexture(oldest.Value.Texture.Id);
            _shadowTextures.Remove(oldest.Key);
            _shadowCacheBytes -= oldest.Value.ByteSize;
        }

        _shadowTextures[key] = shadow;
        _shadowCacheBytes += shadow.ByteSize;
    }

    private static float RoundedRectOutsideDistance(
        float x,
        float y,
        float width,
        float height,
        BorderRadius radius)
    {
        if (x < radius.TopLeft && y < radius.TopLeft)
        {
            return MathF.Sqrt(MathF.Pow(x - radius.TopLeft, 2) + MathF.Pow(y - radius.TopLeft, 2)) - radius.TopLeft;
        }

        if (x > width - radius.TopRight && y < radius.TopRight)
        {
            return MathF.Sqrt(MathF.Pow(x - (width - radius.TopRight), 2) + MathF.Pow(y - radius.TopRight, 2)) - radius.TopRight;
        }

        if (x > width - radius.BottomRight && y > height - radius.BottomRight)
        {
            return MathF.Sqrt(MathF.Pow(x - (width - radius.BottomRight), 2) + MathF.Pow(y - (height - radius.BottomRight), 2)) - radius.BottomRight;
        }

        if (x < radius.BottomLeft && y > height - radius.BottomLeft)
        {
            return MathF.Sqrt(MathF.Pow(x - radius.BottomLeft, 2) + MathF.Pow(y - (height - radius.BottomLeft), 2)) - radius.BottomLeft;
        }

        var dx = MathF.Max(MathF.Max(-x, x - width), 0.0f);
        var dy = MathF.Max(MathF.Max(-y, y - height), 0.0f);
        return dx == 0.0f && dy == 0.0f ? -1.0f : MathF.Sqrt(dx * dx + dy * dy);
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

    private void DrawRoundedBorder(Rect rect, BorderRadius radius, Insets thickness, Color color)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f || thickness.Max <= 0.0f)
        {
            return;
        }

        thickness = new Insets(
            MathF.Max(0.0f, thickness.Top),
            MathF.Max(0.0f, thickness.Right),
            MathF.Max(0.0f, thickness.Bottom),
            MathF.Max(0.0f, thickness.Left));

        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        var innerRect = rect.Inset(thickness);
        if (innerRect.Width <= 0.0f || innerRect.Height <= 0.0f)
        {
            DrawRoundedRect(rect.X, rect.Y, rect.Width, rect.Height, radius, color);
            return;
        }

        var outer = BuildRoundedContour(rect, radius);
        var innerRadius = ClampCornerRadius(radius.Inset(thickness), innerRect.Width, innerRect.Height);
        var inner = BuildRoundedContour(innerRect, innerRadius);
        var vertices = new float[outer.Length * 6 * 6];
        var vertex = 0;

        for (var i = 0; i < outer.Length; i++)
        {
            var next = (i + 1) % outer.Length;
            WriteVertex(vertices, vertex++, outer[i].X, outer[i].Y, color);
            WriteVertex(vertices, vertex++, outer[next].X, outer[next].Y, color);
            WriteVertex(vertices, vertex++, inner[next].X, inner[next].Y, color);
            WriteVertex(vertices, vertex++, outer[i].X, outer[i].Y, color);
            WriteVertex(vertices, vertex++, inner[next].X, inner[next].Y, color);
            WriteVertex(vertices, vertex++, inner[i].X, inner[i].Y, color);
        }

        DrawVertices(vertices, PrimitiveType.Triangles);
    }

    private static (float X, float Y)[] BuildRoundedContour(Rect rect, BorderRadius radius)
    {
        const int SEGMENTS = 16;
        var points = new (float X, float Y)[4 * (SEGMENTS + 1)];
        var index = 0;

        AddContourCorner(points, ref index, rect.X + rect.Width - radius.TopRight,
            rect.Y + radius.TopRight, radius.TopRight, -90.0f, 0.0f, rect.X + rect.Width, rect.Y);
        AddContourCorner(points, ref index, rect.X + rect.Width - radius.BottomRight,
            rect.Y + rect.Height - radius.BottomRight, radius.BottomRight, 0.0f, 90.0f,
            rect.X + rect.Width, rect.Y + rect.Height);
        AddContourCorner(points, ref index, rect.X + radius.BottomLeft,
            rect.Y + rect.Height - radius.BottomLeft, radius.BottomLeft, 90.0f, 180.0f,
            rect.X, rect.Y + rect.Height);
        AddContourCorner(points, ref index, rect.X + radius.TopLeft,
            rect.Y + radius.TopLeft, radius.TopLeft, 180.0f, 270.0f, rect.X, rect.Y);

        return points;
    }

    private static void AddContourCorner(
        (float X, float Y)[] points,
        ref int index,
        float cx,
        float cy,
        float radius,
        float fromDegrees,
        float toDegrees,
        float sharpX,
        float sharpY)
    {
        const int SEGMENTS = 16;
        for (var i = 0; i <= SEGMENTS; i++)
        {
            if (radius <= 0.0f)
            {
                points[index++] = (sharpX, sharpY);
                continue;
            }

            var degrees = fromDegrees + (toDegrees - fromDegrees) * i / SEGMENTS;
            var radians = degrees * MathF.PI / 180.0f;
            points[index++] = (cx + MathF.Cos(radians) * radius, cy + MathF.Sin(radians) * radius);
        }
    }

    private void DrawRoundedGradient(
        Rect rect,
        BorderRadius radius,
        Gradient gradient,
        GradientDirection direction,
        float offset)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        offset -= MathF.Floor(offset);

        if (direction == GradientDirection.Vertical)
        {
            DrawVerticalGradient(rect, radius, gradient, offset);
            return;
        }

        DrawHorizontalGradient(rect, radius, gradient, offset);
    }

    private void DrawHorizontalGradient(Rect rect, BorderRadius radius, Gradient gradient, float offset)
    {
        var strips = Math.Max(16, Math.Min(150, (int)MathF.Ceiling(rect.Width)));
        var stripWidth = rect.Width / strips;

        for (var i = 0; i < strips; i++)
        {
            var x0 = rect.X + i * stripWidth;
            var x1 = i == strips - 1 ? rect.X + rect.Width : x0 + stripWidth;
            var centerX = x0 + (x1 - x0) * 0.5f - rect.X;
            var top = rect.Y + RoundedTopInset(centerX, rect.Width, radius);
            var bottom = rect.Y + rect.Height - RoundedBottomInset(centerX, rect.Width, radius);
            var height = bottom - top;
            if (height <= 0)
            {
                continue;
            }

            var position = GradientPosition(i, strips, offset);
            DrawRect(x0, top, x1 - x0, height, gradient.Evaluate(position));
        }
    }

    private void DrawVerticalGradient(Rect rect, BorderRadius radius, Gradient gradient, float offset)
    {
        var strips = Math.Max(16, Math.Min(150, (int)MathF.Ceiling(rect.Height)));
        var stripHeight = rect.Height / strips;

        for (var i = 0; i < strips; i++)
        {
            var y0 = rect.Y + i * stripHeight;
            var y1 = i == strips - 1 ? rect.Y + rect.Height : y0 + stripHeight;
            var centerY = y0 + (y1 - y0) * 0.5f - rect.Y;
            var left = rect.X + RoundedLeftInset(centerY, rect.Height, radius);
            var right = rect.X + rect.Width - RoundedRightInset(centerY, rect.Height, radius);
            var width = right - left;
            if (width <= 0)
            {
                continue;
            }

            var position = GradientPosition(i, strips, offset);
            DrawRect(left, y0, width, y1 - y0, gradient.Evaluate(position));
        }
    }

    private static float GradientPosition(int strip, int stripCount, float offset)
    {
        var position = (float)strip / Math.Max(1, stripCount - 1);
        if (offset == 0.0f)
        {
            return position;
        }

        position += offset;
        return position - MathF.Floor(position);
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

    private static float RoundedLeftInset(float y, float height, BorderRadius radius)
    {
        if (y < radius.TopLeft && radius.TopLeft > 0.0f)
        {
            return CircleInset(radius.TopLeft, radius.TopLeft - y);
        }

        if (y > height - radius.BottomLeft && radius.BottomLeft > 0.0f)
        {
            return CircleInset(radius.BottomLeft, y - (height - radius.BottomLeft));
        }

        return 0.0f;
    }

    private static float RoundedRightInset(float y, float height, BorderRadius radius)
    {
        if (y < radius.TopRight && radius.TopRight > 0.0f)
        {
            return CircleInset(radius.TopRight, radius.TopRight - y);
        }

        if (y > height - radius.BottomRight && radius.BottomRight > 0.0f)
        {
            return CircleInset(radius.BottomRight, y - (height - radius.BottomRight));
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
        FlushColoredGeometry();
        _font.DrawText(text, x, y, fontSize, charDistance, color);
    }

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

        var texture = _textureRepository.GetTexture(
            imagePath,
            Math.Max(1, (int)MathF.Ceiling(rect.Width * 2)),
            Math.Max(1, (int)MathF.Ceiling(rect.Height * 2)),
            loadAsync);
        if (texture is null)
        {
            return;
        }

        DrawTexture(texture.Value, rect, multiplicativeColor, _textureProgram, _textureViewportLocation,
            _textureLocation, _textureColorLocation, rotationRadians);
    }

    public void DrawImage(RawImageData image, Rect rect, Color multiplicativeColor, float rotationRadians = 0)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var texture = _textureRepository.GetTexture(image);
        DrawTexture(texture, rect, multiplicativeColor, _textureProgram, _textureViewportLocation,
            _textureLocation, _textureColorLocation, rotationRadians);
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
            DrawTexture(texture.Value, rect, multiplicativeColor, _textureProgram, _textureViewportLocation,
                _textureLocation, _textureColorLocation, rotationRadians);
        }
    }

    private void DrawTexture(
        Texture texture,
        Rect rect,
        Color color,
        uint program,
        int viewportLocation,
        int textureLocation,
        int colorLocation,
        float rotationRadians = 0)
    {
        FlushColoredGeometry();

        var x = rect.X;
        var y = rect.Y;
        var width = rect.Width;
        var height = rect.Height;
        var topLeft = RotatePoint(x, y, rect, rotationRadians);
        var topRight = RotatePoint(x + width, y, rect, rotationRadians);
        var bottomRight = RotatePoint(x + width, y + height, rect, rotationRadians);
        var bottomLeft = RotatePoint(x, y + height, rect, rotationRadians);
        Span<float> vertices =
        [
            topLeft.X, topLeft.Y, 0.0f, 0.0f,
            topRight.X, topRight.Y, 1.0f, 0.0f,
            bottomRight.X, bottomRight.Y, 1.0f, 1.0f,
            topLeft.X, topLeft.Y, 0.0f, 0.0f,
            bottomRight.X, bottomRight.Y, 1.0f, 1.0f,
            bottomLeft.X, bottomLeft.Y, 0.0f, 1.0f,
        ];

        _gl.UseProgram(program);
        _gl.Uniform2(viewportLocation, (float)Width, (float)Height);
        _gl.Uniform1(textureLocation, 0);
        _gl.Uniform4(
            colorLocation,
            color.R,
            color.G,
            color.B,
            color.A);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, texture.Id);
        _gl.BindVertexArray(_textureVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textureVbo);
        fixed (float* data = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
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
            DrawTexture(texture.Value, rect, renderedColor, _textureProgram, _textureViewportLocation,
                _textureLocation, _textureColorLocation, rotationRadians);
        }
        else
        {
            DrawTexture(texture.Value, rect, renderedColor, _svgTextureProgram, _svgTextureViewportLocation,
                _svgTextureLocation, _svgTextureColorLocation, rotationRadians);
        }
    }

    private void DrawVertices(ReadOnlySpan<float> vertices, PrimitiveType primitiveType)
    {
        const int FLOATS_PER_VERTEX = 6;
        var vertexCount = vertices.Length / FLOATS_PER_VERTEX;

        switch (primitiveType)
        {
            case PrimitiveType.Triangles:
                AppendColoredVertices(vertices);
                break;
            case PrimitiveType.TriangleFan:
                _coloredVertices.EnsureCapacity(_coloredVertices.Count + Math.Max(0, vertexCount - 2) * 3 * FLOATS_PER_VERTEX);
                for (var vertex = 1; vertex < vertexCount - 1; vertex++)
                {
                    AppendColoredVertices(vertices[..FLOATS_PER_VERTEX]);
                    AppendColoredVertices(vertices.Slice(vertex * FLOATS_PER_VERTEX, FLOATS_PER_VERTEX));
                    AppendColoredVertices(vertices.Slice((vertex + 1) * FLOATS_PER_VERTEX, FLOATS_PER_VERTEX));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(primitiveType), primitiveType, "Unsupported colored primitive type.");
        }
    }

    private void AppendColoredVertices(ReadOnlySpan<float> vertices)
    {
        _coloredVertices.EnsureCapacity(_coloredVertices.Count + vertices.Length);
        foreach (var value in vertices)
        {
            _coloredVertices.Add(value);
        }
    }

    private void FlushColoredGeometry()
    {
        if (_coloredVertices.Count == 0)
        {
            return;
        }

        _gl.UseProgram(_program);
        _gl.Uniform2(_viewportLocation, (float)Width, (float)Height);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        var vertices = CollectionsMarshal.AsSpan(_coloredVertices);
        fixed (float* data = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(vertices.Length / 6));
        _coloredVertices.Clear();
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

        const int SEGMENTS = 16;
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushColoredGeometry();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_textureVbo);
        _gl.DeleteVertexArray(_textureVao);
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_textureProgram);
        _gl.DeleteProgram(_svgTextureProgram);
        foreach (var shadow in _shadowTextures.Values)
        {
            _gl.DeleteTexture(shadow.Texture.Id);
        }
        _shadowTextures.Clear();
        _shadowCacheBytes = 0;
        _textureRepository.Dispose();
        _font.Dispose();
        _disposed = true;
    }
}

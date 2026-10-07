using System.Diagnostics;
using System.Runtime.InteropServices;
using HyprNetShell.Rendering.Primitives;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

public sealed unsafe class Renderer : IRenderApi, IDisposable
{
    private const int MAX_GRADIENT_STOPS = 64;
    private const float MAX_SHADOW_DISTANCE = 256.0f;

    private const int ROUNDED_MODE_SOLID = 0;
    private const int ROUNDED_MODE_BORDER = 1;
    private const int ROUNDED_MODE_GRADIENT = 2;
    private const int ROUNDED_MODE_SHADOW = 3;

    private readonly GL _gl;

    private readonly uint _program;

    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly List<float> _coloredVertices = new(6 * 6);

    private readonly int _viewportLocation;

    private readonly uint _roundedProgram;
    private readonly uint _roundedVao;
    private readonly uint _roundedVbo;
    private readonly uint _roundedInstanceVbo;
    private const int ROUNDED_INSTANCE_FLOATS = 24;
    private readonly List<float> _roundedInstances = new();
    private readonly List<float> _textureVertices = new();
    private uint _batchTexture;
    private uint _batchTextureProgram;
    private int _batchTextureViewportLocation;
    private int _batchTextureLocation;
    private readonly int _roundedViewportLocation;
    private readonly int _roundedGradientDirectionLocation;
    private readonly int _roundedGradientOffsetLocation;
    private readonly int _roundedGradientStopCountLocation;
    private readonly int _roundedGradientPositionsLocation;
    private readonly int _roundedGradientColorsLocation;

    private readonly uint _textureProgram;
    private readonly uint _textureVao;
    private readonly uint _textureVbo;
    private readonly int _textureViewportLocation;
    private readonly int _textureLocation;
    private readonly int _textureVertexColorLocation;

    private readonly uint _svgTextureProgram;
    private readonly int _svgTextureViewportLocation;
    private readonly int _svgTextureLocation;
    private readonly int _svgTextureVertexColorLocation;

    private readonly uint _imageShadowProgram;
    private readonly int _imageShadowViewportLocation;
    private readonly int _imageShadowTextureLocation;
    private readonly int _imageShadowBlurStepLocation;

    private readonly TextureRepository _textureRepository;

    private readonly FontRenderer _font;

    private bool _disposed;
    private bool _diagnosticsEnabled;
    private long _coloredDrawRequests;
    private long _coloredVerticesCount;
    private long _textDraws;
    private long _textureDraws;
    private long _coloredFlushes;
    private long _glDrawCalls;
    private long _bufferUploads;
    private long _bufferUploadBytes;
    private long _roundedRects;
    private long _roundedBorders;
    private long _shadows;

    public int Width
    {
        get; private set;
    }
    public int Height
    {
        get; private set;
    }

    public static int TargetFramerate
    {
        get; private set;
    }
    public static float DeltaTime
    {
        get; private set;
    }

    public static event Action? OnFrameStart;
    public static event Action? OnFrameEnd;

    public Renderer(int targetFramerate, Func<string, IntPtr> getProcAddress)
    {
        TargetFramerate = targetFramerate;
        DeltaTime = 1.0f / TargetFramerate;

        _gl = GL.GetApi(getProcAddress);
        _program = GlShaders.CreateProgram(_gl, GlShaders.COLORED_VERTEX, GlShaders.COLORED_FRAGMENT, "colored");
        _viewportLocation = _gl.GetUniformLocation(_program, "uViewport");

        _roundedProgram = GlShaders.CreateProgram(
            _gl, GlShaders.ROUNDED_VERTEX, GlShaders.ROUNDED_FRAGMENT, "rounded shape");
        _roundedViewportLocation = _gl.GetUniformLocation(_roundedProgram, "uViewport");
        _roundedGradientDirectionLocation = _gl.GetUniformLocation(_roundedProgram, "uGradientDirection");
        _roundedGradientOffsetLocation = _gl.GetUniformLocation(_roundedProgram, "uGradientOffset");
        _roundedGradientStopCountLocation = _gl.GetUniformLocation(_roundedProgram, "uGradientStopCount");
        _roundedGradientPositionsLocation = _gl.GetUniformLocation(_roundedProgram, "uGradientPositions[0]");
        _roundedGradientColorsLocation = _gl.GetUniformLocation(_roundedProgram, "uGradientColors[0]");

        _textureProgram = GlShaders.CreateProgram(_gl, GlShaders.TEXTURED_VERTEX, GlShaders.TEXTURE_FRAGMENT, "texture");
        _textureViewportLocation = _gl.GetUniformLocation(_textureProgram, "uViewport");
        _textureLocation = _gl.GetUniformLocation(_textureProgram, "uTexture");
        _textureVertexColorLocation = _gl.GetUniformLocation(_textureProgram, "uUseVertexColor");
        _svgTextureProgram = GlShaders.CreateProgram(
            _gl, GlShaders.TEXTURED_VERTEX, GlShaders.SVG_TEXTURE_FRAGMENT, "SVG texture");
        _svgTextureViewportLocation = _gl.GetUniformLocation(_svgTextureProgram, "uViewport");
        _svgTextureLocation = _gl.GetUniformLocation(_svgTextureProgram, "uTexture");
        _svgTextureVertexColorLocation = _gl.GetUniformLocation(_svgTextureProgram, "uUseVertexColor");

        _imageShadowProgram = GlShaders.CreateProgram(
            _gl, GlShaders.TEXTURED_VERTEX, GlShaders.IMAGE_SHADOW_FRAGMENT, "image shadow");
        _imageShadowViewportLocation = _gl.GetUniformLocation(_imageShadowProgram, "uViewport");
        _imageShadowTextureLocation = _gl.GetUniformLocation(_imageShadowProgram, "uTexture");
        _imageShadowBlurStepLocation = _gl.GetUniformLocation(_imageShadowProgram, "uBlurStep");

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

        _roundedVao = _gl.GenVertexArray();
        _roundedVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_roundedVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _roundedVbo);
        ReadOnlySpan<float> unitQuad = [0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1];
        fixed (float* data = unitQuad)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(unitQuad.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
        }
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 2 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _roundedInstanceVbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _roundedInstanceVbo);
        int[] attributeSizes = [4, 4, 4, 4, 4, 4];
        var attributeOffset = 0;
        for (uint attribute = 1; attribute <= attributeSizes.Length; attribute++)
        {
            var size = attributeSizes[attribute - 1];
            _gl.VertexAttribPointer(attribute, size, VertexAttribPointerType.Float, false,
                ROUNDED_INSTANCE_FLOATS * sizeof(float), (void*)(attributeOffset * sizeof(float)));
            _gl.EnableVertexAttribArray(attribute);
            _gl.VertexAttribDivisor(attribute, 1);
            attributeOffset += size;
        }

        _textureVao = _gl.GenVertexArray();
        _textureVbo = _gl.GenBuffer();
        _gl.BindVertexArray(_textureVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textureVbo);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, 8 * sizeof(float), (void*)(4 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);

        _font = new FontRenderer(_gl);
        _textureRepository = new TextureRepository(_gl);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFuncSeparate(
            BlendingFactor.SrcAlpha,
            BlendingFactor.OneMinusSrcAlpha,
            BlendingFactor.One,
            BlendingFactor.OneMinusSrcAlpha);
    }

    public void BeginFrame(int width, int height) => BeginFrame(width, height, new Color(0, 0, 0, 0));

    public void BeginFrame(int width, int height, Color clearColor)
    {
        FlushPendingGeometry();
        ResetFrameMetrics();
        _textureRepository.RemoveUnusedPathResources();

        Width = Math.Max(width, 1);
        Height = Math.Max(height, 1);

        _gl.Viewport(0, 0, (uint)Width, (uint)Height);
        _gl.ClearColor(clearColor.R, clearColor.G, clearColor.B, clearColor.A);
        _gl.Clear(ClearBufferMask.ColorBufferBit);
        _gl.UseProgram(_program);
        _gl.Uniform2(_viewportLocation, (float)Width, (float)Height);
        _font.SetViewport(Width, Height);

        OnFrameStart?.Invoke();
    }

    public void EndFrame()
    {
        OnFrameEnd?.Invoke();
        FlushPendingGeometry();
        _gl.Flush();
    }

    /// <summary>
    /// Call once after all output and overlay frames, with the renderer's GL context current.
    /// Evicts keyed images unused across the entire application frame.
    /// </summary>
    public void EndApplicationFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        FlushPendingGeometry();
        _textureRepository.RemoveUnusedLiveResources();
    }

    [Conditional(PerformanceProfiling.Symbol)]
    public void SetDiagnosticsEnabled(bool enabled) => _diagnosticsEnabled = enabled;

    public RendererFrameMetrics GetFrameMetrics() => new(
        _coloredDrawRequests,
        _coloredVerticesCount,
        _textDraws,
        _textureDraws,
        _coloredFlushes,
        _glDrawCalls,
        _bufferUploads,
        _bufferUploadBytes,
        _roundedRects,
        _roundedBorders,
        _shadows);

    public float MeasureText(string text, float fontSize) => _font.MeasureText(text, fontSize);

    public void FillRect(Rect rect, Color color) => DrawRect(rect.X, rect.Y, rect.Width, rect.Height, color);

    public void FillRoundedRect(Rect rect, float radius, Color color)
        => FillRoundedRect(rect, new BorderRadius(radius), color);

    public void FillRoundedRect(Rect rect, BorderRadius radius, Color color)
    {
        RecordRoundedRect();
        DrawRoundedRect(rect.X, rect.Y, rect.Width, rect.Height, radius, color);
    }

    public void FillRoundedShadow(Rect rect, BorderRadius radius, Color color, float distance)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f || color.A <= 0.0f ||
            !float.IsFinite(distance) || distance <= 0.0f)
        {
            return;
        }

        RecordShadow();
        distance = MathF.Min(distance, MAX_SHADOW_DISTANCE);
        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        DrawRoundedShape(rect, radius, color, ROUNDED_MODE_SHADOW, default, default, null, distance);
    }

    public void FillRoundedBorder(Rect rect, BorderRadius radius, Insets thickness, Color color)
    {
        RecordRoundedBorder();
        DrawRoundedBorder(rect, radius, thickness, color);
    }

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





    private void DrawRoundedRect(float x, float y, float width, float height, BorderRadius radius, Color color)
    {
        if (width <= 0.0f || height <= 0.0f)
        {
            return;
        }

        radius = ClampCornerRadius(radius, width, height);
        DrawRoundedShape(new Rect(x, y, width, height), radius, color, ROUNDED_MODE_SOLID);
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

        var innerRadius = ClampCornerRadius(radius.Inset(thickness), innerRect.Width, innerRect.Height);
        DrawRoundedShape(rect, radius, color, ROUNDED_MODE_BORDER, thickness, innerRadius);
    }

    private void DrawRoundedGradient(
        Rect rect,
        BorderRadius radius,
        Gradient gradient,
        GradientDirection direction,
        float offset)
    {
        if (rect.Width <= 0.0f || rect.Height <= 0.0f)
        {
            return;
        }

        if (gradient.Stops.Count > MAX_GRADIENT_STOPS)
        {
            throw new ArgumentException(
                $"Rounded gradients support at most {MAX_GRADIENT_STOPS} stops.",
                nameof(gradient));
        }

        radius = ClampCornerRadius(radius, rect.Width, rect.Height);
        offset -= MathF.Floor(offset);
        DrawRoundedShape(
            rect,
            radius,
            Color.White,
            ROUNDED_MODE_GRADIENT,
            gradient: gradient,
            gradientDirection: direction,
            gradientOffset: offset);
    }

    private void DrawRoundedShape(
        Rect rect,
        BorderRadius radius,
        Color color,
        int mode,
        Insets thickness = default,
        BorderRadius innerRadius = default,
        Gradient? gradient = null,
        float shadowDistance = 0.0f,
        GradientDirection gradientDirection = GradientDirection.Horizontal,
        float gradientOffset = 0.0f)
    {
        FlushColoredGeometry();
        FlushTextureGeometry();
        if (gradient is not null)
        {
            FlushRoundedGeometry();
        }

        var padding = mode == ROUNDED_MODE_SHADOW ? MathF.Ceiling(shadowDistance) + 1.0f : 0.0f;
        ReadOnlySpan<float> instance =
        [
            rect.X, rect.Y, rect.Width, rect.Height,
            radius.TopLeft, radius.TopRight, radius.BottomRight, radius.BottomLeft,
            color.R, color.G, color.B, color.A,
            thickness.Top, thickness.Right, thickness.Bottom, thickness.Left,
            innerRadius.TopLeft, innerRadius.TopRight, innerRadius.BottomRight, innerRadius.BottomLeft,
            mode, MathF.Max(shadowDistance, 0.0001f), padding, 0,
        ];
        _roundedInstances.AddRange(instance);
        RecordColoredDraw(6);
        if (gradient is null)
        {
            return;
        }

        _gl.UseProgram(_roundedProgram);
        _gl.Uniform1(_roundedGradientDirectionLocation, (int)gradientDirection);
        _gl.Uniform1(_roundedGradientOffsetLocation, gradientOffset);

        if (gradient is not null)
        {
            var stopCount = gradient.Stops.Count;
            Span<float> positions = stackalloc float[stopCount];
            Span<float> colors = stackalloc float[stopCount * 4];
            for (var i = 0; i < stopCount; i++)
            {
                var stop = gradient.Stops[i];
                positions[i] = stop.Percent;
                colors[i * 4] = stop.Color.R;
                colors[i * 4 + 1] = stop.Color.G;
                colors[i * 4 + 2] = stop.Color.B;
                colors[i * 4 + 3] = stop.Color.A;
            }

            _gl.Uniform1(_roundedGradientStopCountLocation, stopCount);
            fixed (float* positionData = positions)
            fixed (float* colorData = colors)
            {
                _gl.Uniform1(_roundedGradientPositionsLocation, (uint)stopCount, positionData);
                _gl.Uniform4(_roundedGradientColorsLocation, (uint)stopCount, colorData);
            }
        }
        else
        {
            _gl.Uniform1(_roundedGradientStopCountLocation, 0);
        }

        FlushRoundedGeometry();
    }

    private void FlushRoundedGeometry()
    {
        if (_roundedInstances.Count == 0)
        {
            return;
        }

        _gl.UseProgram(_roundedProgram);
        _gl.Uniform2(_roundedViewportLocation, (float)Width, (float)Height);
        _gl.BindVertexArray(_roundedVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _roundedInstanceVbo);
        var instances = CollectionsMarshal.AsSpan(_roundedInstances);
        fixed (float* data = instances)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(instances.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }
        RecordBufferUpload(instances.Length * sizeof(float));
        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)(instances.Length / ROUNDED_INSTANCE_FLOATS));
        _roundedInstances.Clear();
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
        RecordTextDraw();
        FlushPendingGeometry();
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

        DrawTexture(texture.Value, rect, multiplicativeColor, _textureProgram, _textureViewportLocation,
            _textureLocation, rotationRadians);
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
        DrawTexture(texture, rect, multiplicativeColor, _textureProgram, _textureViewportLocation,
            _textureLocation, rotationRadians);
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
                _textureLocation, rotationRadians);
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
        _gl.UseProgram(_imageShadowProgram);
        _gl.Uniform2(_imageShadowBlurStepLocation,
            blurRadius / (4 * rect.Width), blurRadius / (4 * rect.Height));
        RecordShadow();
        DrawTexture(texture, paddedRect, color, _imageShadowProgram,
            _imageShadowViewportLocation, _imageShadowTextureLocation,
            uvPaddingX: padding / rect.Width, uvPaddingY: padding / rect.Height);
        // Blur uniforms belong to this draw, not a later texture batch.
        FlushTextureGeometry();
    }

    private void DrawTexture(
        Texture texture,
        Rect rect,
        Color color,
        uint program,
        int viewportLocation,
        int textureLocation,
        float rotationRadians = 0,
        float uvPaddingX = 0,
        float uvPaddingY = 0)
    {
        RecordTextureDraw();
        FlushColoredGeometry();
        FlushRoundedGeometry();
        if (_textureVertices.Count != 0 && (_batchTexture != texture.Id || _batchTextureProgram != program))
        {
            FlushTextureGeometry();
        }
        _batchTexture = texture.Id;
        _batchTextureProgram = program;
        _batchTextureViewportLocation = viewportLocation;
        _batchTextureLocation = textureLocation;

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
        _textureVertices.AddRange(vertices);
    }

    private void FlushTextureGeometry()
    {
        if (_textureVertices.Count == 0)
        {
            return;
        }

        _gl.UseProgram(_batchTextureProgram);
        _gl.Uniform2(_batchTextureViewportLocation, (float)Width, (float)Height);
        _gl.Uniform1(_batchTextureLocation, 0);
        if (_batchTextureProgram != _imageShadowProgram)
        {
            _gl.Uniform1(_batchTextureProgram == _textureProgram ? _textureVertexColorLocation : _svgTextureVertexColorLocation, 1);
        }
        // Uploads and font measurement can change bindings while this batch is pending.
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _batchTexture);
        _gl.BindVertexArray(_textureVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textureVbo);
        var vertices = CollectionsMarshal.AsSpan(_textureVertices);
        fixed (float* data = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }
        RecordBufferUpload(vertices.Length * sizeof(float));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(vertices.Length / 8));
        _textureVertices.Clear();
    }

    private void FlushPendingGeometry()
    {
        FlushColoredGeometry();
        FlushRoundedGeometry();
        FlushTextureGeometry();
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
                _textureLocation, rotationRadians);
        }
        else
        {
            DrawTexture(texture.Value, rect, renderedColor, _svgTextureProgram, _svgTextureViewportLocation,
                _svgTextureLocation, rotationRadians);
        }
    }

    private void BeginColoredGeometry(int vertexCount)
    {
        FlushRoundedGeometry();
        FlushTextureGeometry();
        RecordColoredDraw(vertexCount);
        _coloredVertices.EnsureCapacity(_coloredVertices.Count + vertexCount * 6);
    }

    private void AppendColoredTriangle(Point first, Point second, Point third, Color color)
    {
        AppendColoredVertex(first, color);
        AppendColoredVertex(second, color);
        AppendColoredVertex(third, color);
    }

    private void AppendColoredVertex(Point point, Color color)
    {
        _coloredVertices.Add(point.X);
        _coloredVertices.Add(point.Y);
        _coloredVertices.Add(color.R);
        _coloredVertices.Add(color.G);
        _coloredVertices.Add(color.B);
        _coloredVertices.Add(color.A);
    }

    private void DrawVertices(ReadOnlySpan<float> vertices, PrimitiveType primitiveType)
    {
        FlushRoundedGeometry();
        FlushTextureGeometry();
        const int FLOATS_PER_VERTEX = 6;
        var vertexCount = vertices.Length / FLOATS_PER_VERTEX;
        RecordColoredDraw(
            primitiveType == PrimitiveType.TriangleFan
                ? Math.Max(0, vertexCount - 2) * 3
                : vertexCount);

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

        RecordColoredFlush(vertices.Length * sizeof(float));
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(vertices.Length / 6));
        _coloredVertices.Clear();
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordRoundedRect()
    {
        if (_diagnosticsEnabled)
        {
            _roundedRects++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordRoundedBorder()
    {
        if (_diagnosticsEnabled)
        {
            _roundedBorders++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordShadow()
    {
        if (_diagnosticsEnabled)
        {
            _shadows++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordTextDraw()
    {
        if (_diagnosticsEnabled)
        {
            _textDraws++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordTextureDraw()
    {
        if (_diagnosticsEnabled)
        {
            _textureDraws++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordColoredDraw(int vertices)
    {
        if (_diagnosticsEnabled)
        {
            _coloredDrawRequests++;
            _coloredVerticesCount += vertices;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordBufferUpload(int bytes)
    {
        if (_diagnosticsEnabled)
        {
            _bufferUploads++;
            _bufferUploadBytes += bytes;
            _glDrawCalls++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordColoredFlush(int bytes)
    {
        if (_diagnosticsEnabled)
        {
            _coloredFlushes++;
            _bufferUploads++;
            _bufferUploadBytes += bytes;
            _glDrawCalls++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void ResetFrameMetrics()
    {
        _coloredDrawRequests = 0;
        _coloredVerticesCount = 0;
        _textDraws = 0;
        _textureDraws = 0;
        _coloredFlushes = 0;
        _glDrawCalls = 0;
        _bufferUploads = 0;
        _bufferUploadBytes = 0;
        _roundedRects = 0;
        _roundedBorders = 0;
        _shadows = 0;
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



    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushPendingGeometry();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteBuffer(_roundedInstanceVbo);
        _gl.DeleteBuffer(_roundedVbo);
        _gl.DeleteVertexArray(_roundedVao);
        _gl.DeleteBuffer(_textureVbo);
        _gl.DeleteVertexArray(_textureVao);
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_roundedProgram);
        _gl.DeleteProgram(_textureProgram);
        _gl.DeleteProgram(_svgTextureProgram);
        _gl.DeleteProgram(_imageShadowProgram);
        _textureRepository.Dispose();
        _font.Dispose();
        _disposed = true;
    }
}

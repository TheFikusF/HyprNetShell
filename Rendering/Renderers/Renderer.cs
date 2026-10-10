using System.Numerics;

using HyprNetShell.Rendering.Primitives;
using HyprNetShell.Rendering.ShaderModels;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering.Renderers;

public sealed unsafe partial class Renderer : IRenderApi, IDisposable
{
    private readonly GL _gl;

    private bool _disposed;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public static int TargetFramerate { get; private set; }

    public static float DeltaTime { get; private set; }

    public static event Action? OnFrameStart;
    public static event Action? OnFrameEnd;

    public Renderer(int targetFramerate, Func<string, IntPtr> getProcAddress)
    {
        TargetFramerate = targetFramerate;
        DeltaTime = 1.0f / TargetFramerate;

        _gl = GL.GetApi(getProcAddress);
        _crystalSphere = new CrystalSphereShader(_gl);
        _sphericalImage = new SphericalImageShader(_gl);
        _programShader = new ColoredShader(_gl);

        _roundedProgramShader = new RoundedShader(_gl);

        _textureProgramShader = new TextureShader(_gl);
        _svgTextureProgramShader = new SvgTextureShader(_gl);

        _imageShadowProgramShader = new ImageShadowShader(_gl);

        _coloredGeometry = new ColoredGeometryBatch(_gl);

        _roundedGeometry = new RoundedGeometryBatch(_gl);
        _textureGeometry = new TextureGeometryBatch(_gl);

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
        _programShader.Bind();
        _programShader.Viewport = new Vector2(Width, Height);
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

    private void FlushPendingGeometry()
    {
        FlushColoredGeometry();
        FlushRoundedGeometry();
        FlushTextureGeometry();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        FlushPendingGeometry();
        _coloredGeometry.Dispose();
        _roundedGeometry.Dispose();
        _textureGeometry.Dispose();
        _programShader.Dispose();
        _roundedProgramShader.Dispose();
        _textureProgramShader.Dispose();
        _svgTextureProgramShader.Dispose();
        _imageShadowProgramShader.Dispose();
        _crystalSphere.Dispose();
        _sphericalImage.Dispose();
        _textureRepository.Dispose();
        _font.Dispose();
        _disposed = true;
    }
}

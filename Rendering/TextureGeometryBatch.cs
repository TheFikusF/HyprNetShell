using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

internal sealed unsafe class TextureGeometryBatch : IDisposable
{
    private const int VERTEX_FLOATS = 8;

    private readonly GL _gl;
    private readonly uint _textureVao;
    private readonly uint _textureVbo;
    private readonly List<float> _textureVertices = new();

    private bool _disposed;

    public bool HasPendingGeometry => _textureVertices.Count != 0;

    public TextureGeometryBatch(GL gl)
    {
        _gl = gl;
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
    }

    public void Append(ReadOnlySpan<float> values)
    {
        _textureVertices.AddRange(values);
    }

    public int Upload()
    {
        _gl.BindVertexArray(_textureVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _textureVbo);
        var values = CollectionsMarshal.AsSpan(_textureVertices);
        fixed (float* data = values)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(values.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }

        return values.Length * sizeof(float);
    }

    public void Draw()
    {
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_textureVertices.Count / VERTEX_FLOATS));
        _textureVertices.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gl.DeleteBuffer(_textureVbo);
        _gl.DeleteVertexArray(_textureVao);
        _disposed = true;
    }
}

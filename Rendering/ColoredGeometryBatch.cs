using System.Runtime.InteropServices;
using HyprNetShell.Rendering.Primitives;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

internal sealed unsafe class ColoredGeometryBatch : IDisposable
{
    private const int VERTEX_FLOATS = 6;
    private const int INITIAL_VERTEX_COUNT = 6;

    private readonly GL _gl;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly List<float> _coloredVertices = new(INITIAL_VERTEX_COUNT * VERTEX_FLOATS);

    private bool _disposed;

    public bool HasPendingGeometry => _coloredVertices.Count != 0;

    public ColoredGeometryBatch(GL gl)
    {
        _gl = gl;
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(INITIAL_VERTEX_COUNT * VERTEX_FLOATS * sizeof(float)), null, BufferUsageARB.DynamicDraw);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, VERTEX_FLOATS * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, VERTEX_FLOATS * sizeof(float),
            (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);
    }

    public void EnsureVertexCapacity(int vertexCount)
    {
        _coloredVertices.EnsureCapacity(_coloredVertices.Count + vertexCount * VERTEX_FLOATS);
    }

    public void AppendVertex(Point point, Color color)
    {
        _coloredVertices.Add(point.X);
        _coloredVertices.Add(point.Y);
        _coloredVertices.Add(color.R);
        _coloredVertices.Add(color.G);
        _coloredVertices.Add(color.B);
        _coloredVertices.Add(color.A);
    }

    public void Append(ReadOnlySpan<float> vertices)
    {
        _coloredVertices.EnsureCapacity(_coloredVertices.Count + vertices.Length);
        foreach (var value in vertices)
        {
            _coloredVertices.Add(value);
        }
    }

    public int Upload()
    {
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        var vertices = CollectionsMarshal.AsSpan(_coloredVertices);
        fixed (float* data = vertices)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertices.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }

        return vertices.Length * sizeof(float);
    }

    public void Draw()
    {
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_coloredVertices.Count / VERTEX_FLOATS));
        _coloredVertices.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _disposed = true;
    }
}

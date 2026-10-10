using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

internal sealed unsafe class RoundedGeometryBatch : IDisposable
{
    private const int INSTANCE_FLOATS = 24;

    private readonly GL _gl;
    private readonly uint _roundedVao;
    private readonly uint _roundedVbo;
    private readonly uint _roundedInstanceVbo;
    private readonly List<float> _roundedInstances = new();

    private bool _disposed;

    public bool HasPendingGeometry => _roundedInstances.Count != 0;

    public RoundedGeometryBatch(GL gl)
    {
        _gl = gl;
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
                INSTANCE_FLOATS * sizeof(float), (void*)(attributeOffset * sizeof(float)));
            _gl.EnableVertexAttribArray(attribute);
            _gl.VertexAttribDivisor(attribute, 1);
            attributeOffset += size;
        }
    }

    public void Append(ReadOnlySpan<float> values)
    {
        _roundedInstances.AddRange(values);
    }

    public int Upload()
    {
        _gl.BindVertexArray(_roundedVao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _roundedInstanceVbo);
        var values = CollectionsMarshal.AsSpan(_roundedInstances);
        fixed (float* data = values)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(values.Length * sizeof(float)), data, BufferUsageARB.DynamicDraw);
        }

        return values.Length * sizeof(float);
    }

    public void Draw()
    {
        _gl.DrawArraysInstanced(PrimitiveType.Triangles, 0, 6, (uint)(_roundedInstances.Count / INSTANCE_FLOATS));
        _roundedInstances.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _gl.DeleteBuffer(_roundedInstanceVbo);
        _gl.DeleteBuffer(_roundedVbo);
        _gl.DeleteVertexArray(_roundedVao);
        _disposed = true;
    }
}

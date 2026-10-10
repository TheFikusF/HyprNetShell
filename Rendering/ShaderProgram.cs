using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

[AttributeUsage(AttributeTargets.Class)]
public sealed class ShaderAttribute(string vertexPath, string fragmentPath) : Attribute
{
    public string VertexPath { get; } = vertexPath;
    public string FragmentPath { get; } = fragmentPath;
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class UniformAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

public abstract class ShaderProgram : IDisposable
{
    private bool _disposed;

    protected GL Gl
    {
        get;
    }

    public uint Id
    {
        get;
    }

    protected ShaderProgram(GL gl, string vertex, string fragment, string label)
    {
        Gl = gl;
        Id = GlShaders.CreateProgram(gl, vertex, fragment, label);
    }

    public void Bind()
    {
        ThrowIfDisposed();
        Gl.UseProgram(Id);
    }

    protected void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if ((uint)Gl.GetInteger(GetPName.CurrentProgram) == Id)
        {
            Gl.UseProgram(0);
        }

        Gl.DeleteProgram(Id);
        _disposed = true;
    }
}

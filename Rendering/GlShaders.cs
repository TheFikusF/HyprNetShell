using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

internal static class GlShaders
{
    public static uint CreateProgram(GL gl, string vertexShader, string fragmentShader, string label)
    {
        uint vertex = 0;
        uint fragment = 0;
        uint program = 0;
        try
        {
            vertex = CompileShader(gl, ShaderType.VertexShader, vertexShader, label);
            fragment = CompileShader(gl, ShaderType.FragmentShader, fragmentShader, label);
            program = gl.CreateProgram();
            gl.AttachShader(program, vertex);
            gl.AttachShader(program, fragment);
            gl.LinkProgram(program);
            gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var status);
            if (status == 0)
            {
                throw new InvalidOperationException($"OpenGL {label} program link failed: {gl.GetProgramInfoLog(program)}");
            }

            return program;
        }

        catch
        {
            if (program != 0)
            {
                gl.DeleteProgram(program);
            }

            throw;
        }

        finally
        {
            if (fragment != 0)
            {
                gl.DeleteShader(fragment);
            }

            if (vertex != 0)
            {
                gl.DeleteShader(vertex);
            }
        }
    }

    private static uint CompileShader(GL gl, ShaderType type, string source, string label)
    {
        var shader = gl.CreateShader(type);
        try
        {
            gl.ShaderSource(shader, source);
            gl.CompileShader(shader);
            gl.GetShader(shader, ShaderParameterName.CompileStatus, out var status);
            if (status == 0)
            {
                throw new InvalidOperationException($"{type} {label} compile failed: {gl.GetShaderInfoLog(shader)}");
            }

            return shader;
        }

        catch
        {
            gl.DeleteShader(shader);
            throw;
        }
    }
}

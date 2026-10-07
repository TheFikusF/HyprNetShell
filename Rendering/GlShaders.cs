using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

internal static class GlShaders
{
    private const string RESOURCE_PREFIX = "HyprNetShell.Shaders.";

    public static readonly string COLORED_VERTEX = Load("colored.vert.glsl");
    public static readonly string COLORED_FRAGMENT = Load("colored.frag.glsl");
    public static readonly string ROUNDED_VERTEX = Load("rounded.vert.glsl");
    public static readonly string ROUNDED_FRAGMENT = Load("rounded.frag.glsl");
    public static readonly string TEXTURED_VERTEX = Load("textured.vert.glsl");
    public static readonly string TEXTURE_FRAGMENT = Load("texture.frag.glsl");
    public static readonly string SVG_TEXTURE_FRAGMENT = Load("svg-texture.frag.glsl");
    public static readonly string IMAGE_SHADOW_FRAGMENT = Load("image-shadow.frag.glsl");
    public static readonly string ALPHA_TEXTURE_FRAGMENT = Load("alpha-texture.frag.glsl");

    public static uint CreateProgram(GL gl, string vertexShader, string fragmentShader, string label)
    {
        var vs = CompileShader(gl, ShaderType.VertexShader, vertexShader, label);
        var fs = CompileShader(gl, ShaderType.FragmentShader, fragmentShader, label);
        var program = gl.CreateProgram();
        gl.AttachShader(program, vs);
        gl.AttachShader(program, fs);
        gl.LinkProgram(program);
        gl.GetProgram(program, ProgramPropertyARB.LinkStatus, out var status);
        if (status == 0)
        {
            throw new InvalidOperationException($"OpenGL {label} program link failed: {gl.GetProgramInfoLog(program)}");
        }

        gl.DeleteShader(vs);
        gl.DeleteShader(fs);
        return program;
    }

    private static string Load(string fileName)
    {
        var resourceName = RESOURCE_PREFIX + fileName;
        using var stream = typeof(GlShaders).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded GLSL resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static uint CompileShader(GL gl, ShaderType type, string source, string label)
    {
        var shader = gl.CreateShader(type);
        gl.ShaderSource(shader, source);
        gl.CompileShader(shader);
        gl.GetShader(shader, ShaderParameterName.CompileStatus, out var status);
        if (status == 0)
        {
            throw new InvalidOperationException($"{type} {label} compile failed: {gl.GetShaderInfoLog(shader)}");
        }

        return shader;
    }
}

using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering;

internal static class GlShaders
{
    public const string COLORED_VERTEX = """
        #version 330 core
        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec4 aColor;

        uniform vec2 uViewport;
        out vec4 vColor;

        void main()
        {
            vec2 zeroToOne = aPosition / uViewport;
            vec2 clip = zeroToOne * 2.0 - 1.0;
            gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
            vColor = aColor;
        }
        """;

    public const string COLORED_FRAGMENT = """
        #version 330 core
        in vec4 vColor;
        out vec4 FragColor;

        void main()
        {
            FragColor = vColor;
        }
        """;

    public const string TEXTURED_VERTEX = """
        #version 330 core
        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec2 aTexCoord;

        uniform vec2 uViewport;
        out vec2 vTexCoord;

        void main()
        {
            vec2 zeroToOne = aPosition / uViewport;
            vec2 clip = zeroToOne * 2.0 - 1.0;
            gl_Position = vec4(clip.x, -clip.y, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    public const string TEXTURE_FRAGMENT = """
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uTexture;
        out vec4 FragColor;

        void main()
        {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    public const string ALPHA_TEXTURE_FRAGMENT = """
        #version 330 core
        in vec2 vTexCoord;
        uniform sampler2D uAtlas;
        uniform vec4 uColor;
        out vec4 FragColor;

        void main()
        {
            float alpha = texture(uAtlas, vTexCoord).r;
            FragColor = vec4(uColor.rgb, uColor.a * alpha);
        }
        """;

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

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using HyprNetShell.Rendering.Primitives;
using Silk.NET.OpenGL;
using StbTrueTypeSharp;

namespace HyprNetShell.Rendering;

internal sealed unsafe class FontRenderer : IDisposable
{
    private const int SYMBOL_ATLAS_WIDTH = 2048;
    private const int SYMBOL_ATLAS_HEIGHT = 2048;

    private const string PRIMARY_FONT_RESOURCE_NAME = "HyprNetShell.Fonts.0xProto-Regular-NL.ttf";
    private const string FALLBACK_FONT_RESOURCE_NAME = "HyprNetShell.Fonts.LiberationMono-Regular.ttf";
    private const string EMOJI_FONT_RESOURCE_NAME = "HyprNetShell.Fonts.NotoColorEmoji.ttf";

    private readonly Dictionary<string, UnicodeRange[]> _symbolRanges = new()
    {
        [PRIMARY_FONT_RESOURCE_NAME] =
        [
            new(0x0000, 0x01FF), // Basic Latin printable ASCII.
            new(0x2000, 0x206F), // General Punctuation: dashes, smart quotes, bullets, ellipsis, etc.
        ],
        [FALLBACK_FONT_RESOURCE_NAME] =
        [
            new(0x0400, 0x052F), // Cyrillic and Cyrillic Supplement.
            new(0x1C80, 0x1C8F), // Cyrillic Extended-C.
            new(0x2DE0, 0x2DFF), // Cyrillic Extended-A.
            new(0xA640, 0xA69F), // Cyrillic Extended-B.
        ]
    };

    private readonly GL _gl;
    private readonly Dictionary<string, byte[]> _fontBytes;
    private readonly Dictionary<int, SymbolAtlas[]> _symbolAtlases = new();
    private readonly Dictionary<ColorGlyphKey, ColorGlyph> _colorGlyphs = new();
    private readonly uint _program;
    private readonly uint _colorProgram;
    private readonly uint _vao;
    private readonly uint _vbo;
    private readonly int _viewportLocation;
    private readonly int _colorLocation;
    private readonly int _colorViewportLocation;
    private readonly int _colorGlyphColorLocation;
    private bool _disposed;
    private int _viewportWidth = 1;
    private int _viewportHeight = 1;

    public FontRenderer(GL gl)
    {
        _gl = gl;
        _fontBytes = new Dictionary<string, byte[]>
        {
            [PRIMARY_FONT_RESOURCE_NAME] = ReadEmbeddedFont(PRIMARY_FONT_RESOURCE_NAME),
            [FALLBACK_FONT_RESOURCE_NAME] = ReadEmbeddedFont(FALLBACK_FONT_RESOURCE_NAME)
        };

        _program = GlShaders.CreateProgram(_gl, GlShaders.TEXTURED_VERTEX, GlShaders.ALPHA_TEXTURE_FRAGMENT, "text");
        _colorProgram =
            GlShaders.CreateProgram(_gl, GlShaders.TEXTURED_VERTEX, GlShaders.TEXTURE_FRAGMENT, "color text");
        _viewportLocation = _gl.GetUniformLocation(_program, "uViewport");
        _colorLocation = _gl.GetUniformLocation(_program, "uColor");
        _colorViewportLocation = _gl.GetUniformLocation(_colorProgram, "uViewport");
        _colorGlyphColorLocation = _gl.GetUniformLocation(_colorProgram, "uColor");

        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(6 * 4 * sizeof(float)), null, BufferUsageARB.DynamicDraw);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float),
            (void*)(2 * sizeof(float)));
        _gl.EnableVertexAttribArray(1);

        _gl.UseProgram(_program);
        _gl.Uniform1(_gl.GetUniformLocation(_program, "uAtlas"), 0);
        _gl.UseProgram(_colorProgram);
        _gl.Uniform1(_gl.GetUniformLocation(_colorProgram, "uTexture"), 0);
    }

    public void SetViewport(int width, int height)
    {
        _viewportWidth = Math.Max(width, 1);
        _viewportHeight = Math.Max(height, 1);
    }

    public float MeasureText(string text, float fontSize)
    {
        var width = 0.0f;

        foreach (var element in EnumerateTextElements(text))
        {
            if (!TryGetSymbolGlyph(element, fontSize, out var glyph))
            {
                width += GetColorGlyph(element, fontSize)?.Advance ?? fontSize * 0.5f;
                continue;
            }

            width += glyph.Atlas.Chars[glyph.Index].xadvance;
        }

        return width;
    }

    public void DrawText(string text, float x, float y, float fontSize, float charDistance, Color color)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var cursorX = x;
        var cursorY = y;

        _gl.UseProgram(_program);
        _gl.Uniform2(_viewportLocation, (float)_viewportWidth, (float)_viewportHeight);
        _gl.Uniform4(_colorLocation, color.R, color.G, color.B, color.A);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

        foreach (var element in EnumerateTextElements(text))
        {
            if (!TryGetSymbolGlyph(element, fontSize, out var glyph))
            {
                cursorX += DrawColorGlyph(element, cursorX, y, fontSize, color) + charDistance;
                _gl.UseProgram(_program);
                _gl.Uniform2(_viewportLocation, (float)_viewportWidth, (float)_viewportHeight);
                _gl.Uniform4(_colorLocation, color.R, color.G, color.B, color.A);
                _gl.BindVertexArray(_vao);
                _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
                continue;
            }

            _gl.BindTexture(TextureTarget.Texture2D, glyph.Atlas.Texture);

            fixed (StbTrueType.stbtt_bakedchar* chars = glyph.Atlas.Chars)
            {
                var cursorBefore = cursorX;
                var baselineBefore = cursorY;
                if (glyph.Atlas.Chars[glyph.Index].xadvance <= 0.0f)
                {
                    cursorX += fontSize * 0.5f + charDistance;
                    continue;
                }

                StbTrueType.stbtt_aligned_quad quad;
                StbTrueType.stbtt_GetBakedQuad(
                    chars,
                    SYMBOL_ATLAS_WIDTH,
                    SYMBOL_ATLAS_HEIGHT,
                    glyph.Index,
                    &cursorX,
                    &cursorY,
                    &quad,
                    1);

                if (cursorX == cursorBefore && cursorY == baselineBefore)
                {
                    cursorX += fontSize * 0.5f + charDistance;
                    continue;
                }

                DrawQuad(quad);
            }
        }
    }

    private float DrawColorGlyph(string textElement, float x, float baselineY, float fontSize, Color color)
    {
        var glyph = GetColorGlyph(textElement, fontSize);
        if (glyph is null)
        {
            return fontSize * 0.5f;
        }

        var top = MathF.Round(baselineY - glyph.Height + fontSize * 0.5f);
        Span<float> vertices =
        [
            x, top, 0.0f, 0.0f,
            x + glyph.Width, top, 1.0f, 0.0f,
            x + glyph.Width, top + glyph.Height, 1.0f, 1.0f,
            x, top, 0.0f, 0.0f,
            x + glyph.Width, top + glyph.Height, 1.0f, 1.0f,
            x, top + glyph.Height, 0.0f, 1.0f,
        ];

        _gl.UseProgram(_colorProgram);
        _gl.Uniform2(_colorViewportLocation, (float)_viewportWidth, (float)_viewportHeight);
        _gl.Uniform4(
            _colorGlyphColorLocation,
            color.R * color.A,
            color.G * color.A,
            color.B * color.A,
            color.A);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, glyph.Texture);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);

        fixed (float* data = vertices)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(vertices.Length * sizeof(float)), data);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        return glyph.Advance;
    }

    private void DrawQuad(StbTrueType.stbtt_aligned_quad quad)
    {
        Span<float> vertices =
        [
            quad.x0, quad.y0, quad.s0, quad.t0,
            quad.x1, quad.y0, quad.s1, quad.t0,
            quad.x1, quad.y1, quad.s1, quad.t1,
            quad.x0, quad.y0, quad.s0, quad.t0,
            quad.x1, quad.y1, quad.s1, quad.t1,
            quad.x0, quad.y1, quad.s0, quad.t1,
        ];

        fixed (float* data = vertices)
        {
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(vertices.Length * sizeof(float)), data);
        }

        _gl.DrawArrays(PrimitiveType.Triangles, 0, 6);
    }

    private bool TryGetSymbolGlyph(string textElement, float fontSize, out SymbolGlyph glyph)
    {
        glyph = default;
        if (TryGetSingleCodepoint(textElement, out var codepoint) == false)
        {
            return false;
        }

        foreach (var atlas in GetSymbolAtlases(fontSize))
        {
            if (!atlas.Range.Contains(codepoint))
            {
                continue;
            }

            var index = codepoint - atlas.Range.First;
            if (atlas.Chars[index].xadvance <= 0.0f)
            {
                return false;
            }

            glyph = new SymbolGlyph(atlas, index);
            return true;
        }

        return false;
    }

    private SymbolAtlas[] GetSymbolAtlases(float fontSize)
    {
        var pixelSize = Math.Clamp((int)MathF.Round(fontSize), 8, 48);
        if (_symbolAtlases.TryGetValue(pixelSize, out var atlases))
        {
            return atlases;
        }

        atlases = _symbolRanges.SelectMany(x => x.Value
                .Select(j => ImportSymbolsFromFont(x.Key, j, pixelSize)))
            .ToArray();

        _symbolAtlases[pixelSize] = atlases;
        return atlases;
    }

    private SymbolAtlas ImportSymbolsFromFont(string font, UnicodeRange range, int pixelSize)
    {
        var pixels = new byte[SYMBOL_ATLAS_WIDTH * SYMBOL_ATLAS_HEIGHT];
        var chars = new StbTrueType.stbtt_bakedchar[range.Count];
        var result = StbTrueType.stbtt_BakeFontBitmap(
            _fontBytes[font],
            0,
            pixelSize,
            pixels,
            SYMBOL_ATLAS_WIDTH,
            SYMBOL_ATLAS_HEIGHT,
            range.First,
            range.Count,
            chars);

        if (!result)
        {
            throw new InvalidOperationException(
                $"Failed to bake font symbol range U+{range.First:X4}-U+{range.Last:X4} at {pixelSize}px.");
        }

        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);

        fixed (byte* data = pixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.R8,
                SYMBOL_ATLAS_WIDTH,
                SYMBOL_ATLAS_HEIGHT,
                0,
                PixelFormat.Red,
                PixelType.UnsignedByte,
                data);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        return new SymbolAtlas(range, texture, chars);
    }

    private ColorGlyph? GetColorGlyph(string textElement, float fontSize)
    {
        var pixelSize = Math.Clamp((int)MathF.Round(fontSize), 8, 64);
        var key = new ColorGlyphKey(textElement, pixelSize);
        if (_colorGlyphs.TryGetValue(key, out var glyph))
        {
            return glyph;
        }

        var rendered = ColorEmojiRenderer.Render(textElement, pixelSize);
        if (rendered is null)
        {
            return null;
        }

        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.PixelStore(PixelStoreParameter.UnpackAlignment, 4);

        fixed (byte* data = rendered.Pixels)
        {
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba,
                (uint)rendered.Width,
                (uint)rendered.Height,
                0,
                PixelFormat.Bgra,
                PixelType.UnsignedByte,
                data);
        }

        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);

        glyph = new ColorGlyph(texture, rendered.Width, rendered.Height, rendered.Advance);
        _colorGlyphs[key] = glyph;
        return glyph;
    }

    private static byte[] ReadEmbeddedFont(string resourceName)
    {
        var assembly = typeof(FontRenderer).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            throw new FileNotFoundException($"Missing embedded font {resourceName}");
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IEnumerable<string> EnumerateTextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            yield return enumerator.GetTextElement();
        }
    }

    private static bool TryGetSingleCodepoint(string textElement, out int codepoint)
    {
        codepoint = 0;
        if (textElement.Length != 1)
        {
            return false;
        }

        codepoint = textElement[0];
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var atlas in _symbolAtlases.Values.SelectMany(atlases => atlases))
        {
            _gl.DeleteTexture(atlas.Texture);
        }

        foreach (var glyph in _colorGlyphs.Values)
        {
            _gl.DeleteTexture(glyph.Texture);
        }

        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _gl.DeleteProgram(_program);
        _gl.DeleteProgram(_colorProgram);
        _disposed = true;
    }

    private readonly record struct UnicodeRange(int First, int Last)
    {
        public int Count => Last - First + 1;
        public bool Contains(int codepoint) => codepoint >= First && codepoint <= Last;
    }

    private readonly record struct SymbolGlyph(SymbolAtlas Atlas, int Index);

    private readonly record struct ColorGlyphKey(string Text, int PixelSize);

    private sealed record ColorGlyph(uint Texture, int Width, int Height, float Advance);

    private sealed record SymbolAtlas(UnicodeRange Range, uint Texture, StbTrueType.stbtt_bakedchar[] Chars);

    private static class ColorEmojiRenderer
    {
        private const int CAIRO_FORMAT_ARGB32 = 0;
        private const int PANGO_SCALE = 1024;
        private static readonly Lazy<bool> Available = new(CheckAvailable);

        public static RenderedGlyph? Render(string text, int pixelSize)
        {
            if (!Available.Value)
            {
                return null;
            }

            var font = IntPtr.Zero;
            var measureSurface = IntPtr.Zero;
            var measureContext = IntPtr.Zero;
            var measureLayout = IntPtr.Zero;
            var surface = IntPtr.Zero;
            var context = IntPtr.Zero;
            var layout = IntPtr.Zero;

            try
            {
                font = pango_font_description_from_string("Noto Color Emoji");
                pango_font_description_set_absolute_size(font, pixelSize * PANGO_SCALE);
                measureSurface = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, 1, 1);
                measureContext = cairo_create(measureSurface);
                measureLayout = pango_cairo_create_layout(measureContext);
                pango_layout_set_font_description(measureLayout, font);
                SetLayoutText(measureLayout, text);
                pango_layout_get_pixel_size(measureLayout, out var measuredWidth, out var measuredHeight);

                var width = Math.Clamp(measuredWidth + 4, 1, 256);
                var height = Math.Clamp(Math.Max(measuredHeight, pixelSize) + 4, 1, 256);
                surface = cairo_image_surface_create(CAIRO_FORMAT_ARGB32, width, height);
                context = cairo_create(surface);
                layout = pango_cairo_create_layout(context);
                pango_layout_set_font_description(layout, font);
                SetLayoutText(layout, text);
                cairo_move_to(context, 2.0, 2.0);
                pango_cairo_show_layout(context, layout);
                cairo_surface_flush(surface);

                var stride = cairo_image_surface_get_stride(surface);
                var data = cairo_image_surface_get_data(surface);
                if (data == IntPtr.Zero || stride <= 0)
                {
                    return null;
                }

                var pixels = new byte[height * stride];
                Marshal.Copy(data, pixels, 0, pixels.Length);
                return new RenderedGlyph(width, height, pixels, Math.Max(measuredWidth, pixelSize * 0.5f));
            }
            catch
            {
                return null;
            }
            finally
            {
                if (layout != IntPtr.Zero) g_object_unref(layout);
                if (context != IntPtr.Zero) cairo_destroy(context);
                if (surface != IntPtr.Zero) cairo_surface_destroy(surface);
                if (measureLayout != IntPtr.Zero) g_object_unref(measureLayout);
                if (measureContext != IntPtr.Zero) cairo_destroy(measureContext);
                if (measureSurface != IntPtr.Zero) cairo_surface_destroy(measureSurface);
                if (font != IntPtr.Zero) pango_font_description_free(font);
            }
        }

        private static void SetLayoutText(IntPtr layout, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var terminated = new byte[bytes.Length + 1];
            bytes.CopyTo(terminated, 0);
            pango_layout_set_text(layout, terminated, bytes.Length);
        }

        private static bool CheckAvailable()
        {
            return RegisterEmbeddedEmojiFont() || FontconfigHasNotoColorEmoji();
        }

        private static bool RegisterEmbeddedEmojiFont()
        {
            var bytes = ReadEmbeddedFont(EMOJI_FONT_RESOURCE_NAME);
            if (bytes is null)
            {
                return false;
            }

            try
            {
                var fontPath = Path.Combine(Path.GetTempPath(), "hyprnetshell-fonts", "NotoColorEmoji.ttf");
                Directory.CreateDirectory(Path.GetDirectoryName(fontPath)!);
                if (!File.Exists(fontPath) || new FileInfo(fontPath).Length != bytes.Length)
                {
                    File.WriteAllBytes(fontPath, bytes);
                }

                var pathBytes = Encoding.UTF8.GetBytes(fontPath);
                var terminatedPath = new byte[pathBytes.Length + 1];
                pathBytes.CopyTo(terminatedPath, 0);
                return FcConfigAppFontAddFile(IntPtr.Zero, terminatedPath);
            }
            catch
            {
                return false;
            }
        }

        private static bool FontconfigHasNotoColorEmoji()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "fc-match",
                    ArgumentList = { "--format=%{file}", "Noto Color Emoji" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                });

                process?.WaitForExit(250);
                return process is { ExitCode: 0 };
            }
            catch
            {
                return false;
            }
        }

        [DllImport("cairo")]
        private static extern IntPtr cairo_image_surface_create(int format, int width, int height);

        [DllImport("cairo")]
        private static extern void cairo_surface_destroy(IntPtr surface);

        [DllImport("cairo")]
        private static extern void cairo_surface_flush(IntPtr surface);

        [DllImport("cairo")]
        private static extern IntPtr cairo_image_surface_get_data(IntPtr surface);

        [DllImport("cairo")]
        private static extern int cairo_image_surface_get_stride(IntPtr surface);

        [DllImport("cairo")]
        private static extern IntPtr cairo_create(IntPtr surface);

        [DllImport("cairo")]
        private static extern void cairo_destroy(IntPtr context);

        [DllImport("cairo")]
        private static extern void cairo_move_to(IntPtr context, double x, double y);

        [DllImport("pango-1.0")]
        private static extern IntPtr pango_font_description_from_string(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string str);

        [DllImport("pango-1.0")]
        private static extern void pango_font_description_free(IntPtr desc);

        [DllImport("pango-1.0")]
        private static extern void pango_font_description_set_absolute_size(IntPtr desc, double size);

        [DllImport("pango-1.0")]
        private static extern void pango_layout_set_font_description(IntPtr layout, IntPtr desc);

        [DllImport("pango-1.0")]
        private static extern void pango_layout_set_text(IntPtr layout, byte[] text, int length);

        [DllImport("pango-1.0")]
        private static extern void pango_layout_get_pixel_size(IntPtr layout, out int width, out int height);

        [DllImport("pangocairo-1.0")]
        private static extern IntPtr pango_cairo_create_layout(IntPtr context);

        [DllImport("pangocairo-1.0")]
        private static extern void pango_cairo_show_layout(IntPtr context, IntPtr layout);

        [DllImport("gobject-2.0")]
        private static extern void g_object_unref(IntPtr obj);

        [DllImport("fontconfig")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FcConfigAppFontAddFile(IntPtr config, byte[] file);
    }

    private sealed record RenderedGlyph(int Width, int Height, byte[] Pixels, float Advance);
}

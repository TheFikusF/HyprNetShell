using HyprNetShell.Rendering.ShaderModels;
using System.Numerics;

using HyprNetShell.Rendering.Primitives;
using Silk.NET.OpenGL;

namespace HyprNetShell.Rendering.Renderers;

public sealed unsafe partial class Renderer
{
    private readonly ColoredShader _programShader;
    private readonly ColoredGeometryBatch _coloredGeometry;

    public void FillRect(Rect rect, Color color) => DrawRect(rect.X, rect.Y, rect.Width, rect.Height, color);

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

    private void DrawBorder(float x, float y, float width, float height, float thickness, Color color)
    {
        DrawRect(x, y, width, thickness, color);
        DrawRect(x, y + height - thickness, width, thickness, color);
        DrawRect(x, y, thickness, height, color);
        DrawRect(x + width - thickness, y, thickness, height, color);
    }

    private void BeginColoredGeometry(int vertexCount)
    {
        FlushRoundedGeometry();
        FlushTextureGeometry();
        RecordColoredDraw(vertexCount);
        _coloredGeometry.EnsureVertexCapacity(vertexCount);
    }

    private void AppendColoredTriangle(Point first, Point second, Point third, Color color)
    {
        AppendColoredVertex(first, color);
        AppendColoredVertex(second, color);
        AppendColoredVertex(third, color);
    }

    private void AppendColoredVertex(Point point, Color color)
    {
        _coloredGeometry.AppendVertex(point, color);
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
                _coloredGeometry.EnsureVertexCapacity(Math.Max(0, vertexCount - 2) * 3);
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
        _coloredGeometry.Append(vertices);
    }

    private void FlushColoredGeometry()
    {
        if (!_coloredGeometry.HasPendingGeometry)
        {
            return;
        }

        _programShader.Bind();
        _programShader.Viewport = new Vector2(Width, Height);
        var uploadedBytes = _coloredGeometry.Upload();
        RecordColoredFlush(uploadedBytes);
        _coloredGeometry.Draw();
    }
}

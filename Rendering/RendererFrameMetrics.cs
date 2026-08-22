namespace HyprNetShell.Rendering;

public readonly record struct RendererFrameMetrics(
    long ColoredDrawRequests,
    long ColoredVertices,
    long TextDraws,
    long TextureDraws,
    long ColoredFlushes,
    long GlDrawCalls,
    long BufferUploads,
    long BufferUploadBytes,
    long RoundedRects,
    long RoundedBorders,
    long Shadows)
{
    public static RendererFrameMetrics operator +(RendererFrameMetrics left, RendererFrameMetrics right) => new(
        left.ColoredDrawRequests + right.ColoredDrawRequests,
        left.ColoredVertices + right.ColoredVertices,
        left.TextDraws + right.TextDraws,
        left.TextureDraws + right.TextureDraws,
        left.ColoredFlushes + right.ColoredFlushes,
        left.GlDrawCalls + right.GlDrawCalls,
        left.BufferUploads + right.BufferUploads,
        left.BufferUploadBytes + right.BufferUploadBytes,
        left.RoundedRects + right.RoundedRects,
        left.RoundedBorders + right.RoundedBorders,
        left.Shadows + right.Shadows);
}

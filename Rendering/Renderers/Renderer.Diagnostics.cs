using System.Diagnostics;

namespace HyprNetShell.Rendering.Renderers;

public sealed unsafe partial class Renderer
{
    private bool _diagnosticsEnabled;
    private long _coloredDrawRequests;
    private long _coloredVerticesCount;
    private long _textDraws;
    private long _textureDraws;
    private long _coloredFlushes;
    private long _glDrawCalls;
    private long _bufferUploads;
    private long _bufferUploadBytes;
    private long _roundedRects;
    private long _roundedBorders;
    private long _shadows;

    [Conditional(PerformanceProfiling.Symbol)]
    public void SetDiagnosticsEnabled(bool enabled) => _diagnosticsEnabled = enabled;

    public RendererFrameMetrics GetFrameMetrics() => new(
        _coloredDrawRequests,
        _coloredVerticesCount,
        _textDraws,
        _textureDraws,
        _coloredFlushes,
        _glDrawCalls,
        _bufferUploads,
        _bufferUploadBytes,
        _roundedRects,
        _roundedBorders,
        _shadows);

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordRoundedRect()
    {
        if (_diagnosticsEnabled)
        {
            _roundedRects++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordRoundedBorder()
    {
        if (_diagnosticsEnabled)
        {
            _roundedBorders++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordShadow()
    {
        if (_diagnosticsEnabled)
        {
            _shadows++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordTextDraw()
    {
        if (_diagnosticsEnabled)
        {
            _textDraws++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordTextureDraw()
    {
        if (_diagnosticsEnabled)
        {
            _textureDraws++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordColoredDraw(int vertices)
    {
        if (_diagnosticsEnabled)
        {
            _coloredDrawRequests++;
            _coloredVerticesCount += vertices;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordBufferUpload(int bytes)
    {
        if (_diagnosticsEnabled)
        {
            _bufferUploads++;
            _bufferUploadBytes += bytes;
            _glDrawCalls++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void RecordColoredFlush(int bytes)
    {
        if (_diagnosticsEnabled)
        {
            _coloredFlushes++;
            _bufferUploads++;
            _bufferUploadBytes += bytes;
            _glDrawCalls++;
        }
    }

    [Conditional(PerformanceProfiling.Symbol)]
    private void ResetFrameMetrics()
    {
        _coloredDrawRequests = 0;
        _coloredVerticesCount = 0;
        _textDraws = 0;
        _textureDraws = 0;
        _coloredFlushes = 0;
        _glDrawCalls = 0;
        _bufferUploads = 0;
        _bufferUploadBytes = 0;
        _roundedRects = 0;
        _roundedBorders = 0;
        _shadows = 0;
    }
}

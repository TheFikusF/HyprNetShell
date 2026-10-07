using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Models;

public sealed record WindowThumbnailMetadata(ulong Handle, string Title, string AppId, string Identifier, string Address);
public sealed record WindowThumbnailFrame(ulong Handle, RawImageData Image, ulong Revision);
public sealed record WindowThumbnailSnapshot(WindowThumbnailMetadata Window, WindowThumbnailFrame? Frame);

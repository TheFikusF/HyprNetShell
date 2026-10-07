using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Services;

/// <summary>All calls occur on the application main thread. The service owns and disposes the backend.</summary>
public interface IWindowThumbnailBackend : IDisposable
{
    bool IsAvailable
    {
        get;
    }
    /// <summary>Returns the complete current window list, including while capture is disabled.
    /// Handles are stable and must not be reused during this backend's lifetime.
    /// Identifier is opaque; Address is an exact Hyprland client address, never inferred from title/app ID.
    /// </summary>
    IReadOnlyList<WindowThumbnailMetadata> PollWindows();
    /// <summary>Returns new frames only, with strictly increasing revisions per handle.
    /// Pixel memory must remain valid and immutable after return. Capture must start disabled.
    /// </summary>
    IReadOnlyList<WindowThumbnailFrame> PollFrames();
    void SetEnabled(bool enabled);
    void SetWindowVisible(ulong handle, bool visible);
}

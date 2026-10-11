using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Services;

public interface IWindowThumbnailService : IDisposable
{
    bool IsAvailable
    {
        get;
    }
    IReadOnlyList<WindowThumbnailSnapshot> Snapshot
    {
        get;
    }
    event Action<WindowThumbnailSnapshot>? WindowAdded;
    event Action<WindowThumbnailSnapshot>? WindowRemoved;
    void Update();
    /// <summary>A null handle list requests all windows. Missing sizes use 640x360. Owners are compared by reference.</summary>
    void SetOverviewVisible(object owner, string outputName, bool visible, IReadOnlyCollection<ulong>? handles = null,
        IReadOnlyDictionary<ulong, WindowThumbnailSize>? sizes = null);
    void RemoveOwner(object owner);
}

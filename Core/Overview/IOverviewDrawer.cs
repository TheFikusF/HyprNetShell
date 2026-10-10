using HyprNetShell.Core.Bar.Dialogs;
using HyprNetShell.Core.Models;
using HyprNetShell.Rendering;
using HyprNetShell.Rendering.Primitives;

namespace HyprNetShell.Core.Overview;

internal sealed record OverviewPreview(string Address, string Title, string Subtitle, int WorkspaceId,
    Rect Bounds, string? IconPath, int WindowWidth, int WindowHeight)
{
    public WindowThumbnailFrame? Frame
    {
        get; set;
    }

    public Dictionary<DialogKey, string> Neighbors { get; } = [];
}

internal readonly record struct OverviewDrawContext(int Width, int Height, int X, int Y,
    float Progress, float TransitionProgress, bool PreviewTransition, bool Interactive,
    bool PointerOverOverlay, double Now, double DeltaTime, string? FocusedAddress,
    string? SelectedAddress, IReadOnlyList<OverviewPreview> Previews, RawImageData? Background);

internal interface IOverviewDrawer
{
    Rect[] BuildLayout(int width, int height, float[] ratios);
    Point ProjectCenter(Rect bounds, int width, int height);
    string? Draw(IRenderApi renderer, OverviewDrawContext context);
    void DrawCaption(IRenderApi renderer, string text, float x, float y, float width, float size, Color color);
    void Reset();
}

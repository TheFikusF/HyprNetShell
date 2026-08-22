namespace HyprNetShell.GUI.Layout;

public readonly record struct LayoutFrameMetrics(
    long LayoutsCreated,
    long LayoutsDrawn,
    long BoxDraws,
    long WidthMeasurements,
    long HeightMeasurements,
    long ChildArrayAllocations,
    long ChildArrayElements)
{
    public static LayoutFrameMetrics operator +(LayoutFrameMetrics left, LayoutFrameMetrics right) => new(
        left.LayoutsCreated + right.LayoutsCreated,
        left.LayoutsDrawn + right.LayoutsDrawn,
        left.BoxDraws + right.BoxDraws,
        left.WidthMeasurements + right.WidthMeasurements,
        left.HeightMeasurements + right.HeightMeasurements,
        left.ChildArrayAllocations + right.ChildArrayAllocations,
        left.ChildArrayElements + right.ChildArrayElements);
}

using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Assets;

public static partial class OtherAssets
{
    [SvgAsset("assets/svgs/clock.svg")]
    public static partial SvgAsset ClockFace { get; }
    
    [SvgAsset("assets/svgs/sun.svg")]
    public static partial SvgAsset Sun { get; }
    
    [SvgAsset("assets/svgs/moon.svg")]
    public static partial SvgAsset Moon { get; }
}
using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Features.System;

[JsonSerializable(typeof(WallpaperSlideshowConfig))]
internal sealed partial class WallpaperJsonContext : JsonSerializerContext
{
}

internal sealed record WallpaperSlideshowConfig(bool SlideshowEnabled, int DurationMinutes);

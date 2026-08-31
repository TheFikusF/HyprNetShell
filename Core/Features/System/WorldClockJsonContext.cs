using System.Text.Json.Serialization;

namespace HyprNetShell.Core.Features.System;

internal sealed record WorldClockConfigurationDocument(string[] TimeZoneIds);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WorldClockConfigurationDocument))]
internal sealed partial class WorldClockJsonContext : JsonSerializerContext;

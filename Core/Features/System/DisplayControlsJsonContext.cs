using System.Text.Json.Serialization;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

[JsonSerializable(typeof(TemperatureScheduleConfig))]
[JsonSerializable(typeof(TemperatureCurvePoint[]))]
internal sealed partial class DisplayControlsJsonContext : JsonSerializerContext
{
}

internal sealed record TemperatureScheduleConfig(bool Enabled, TemperatureCurvePoint[] Points);

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed partial class AudioModuleService : IBarDataService
{
    public AudioSnapshot Snapshot { get; private set; } = AudioSnapshot.Empty;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var statusTask = CommandRunner.TryReadAsync(
            "wpctl",
            "status",
            TimeSpan.FromMilliseconds(900),
            cancellationToken);
        var graphTask = CommandRunner.TryReadAsync(
            "pw-dump",
            "-N",
            TimeSpan.FromMilliseconds(900),
            cancellationToken);

        await Task.WhenAll(statusTask, graphTask);
        Snapshot = ParseStatus(await statusTask) with { IsRecording = ParseRecordingState(await graphTask) };
    }

    internal Task SetDefaultAsync(string deviceId) =>
        CommandRunner.TryRunAsync(
            "wpctl",
            ["set-default", deviceId],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

    internal Task SetVolumeAsync(string deviceId, int volume) =>
        CommandRunner.TryRunAsync(
            "wpctl",
            ["set-volume", deviceId, $"{Math.Clamp(volume, 0, 100)}%"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

    internal Task SetMutedAsync(string deviceId, bool muted) =>
        CommandRunner.TryRunAsync(
            "wpctl",
            ["set-mute", deviceId, muted ? "1" : "0"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

    internal static AudioSnapshot ParseStatus(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return AudioSnapshot.Empty;
        }

        var outputs = new List<AudioDeviceSnapshot>();
        var inputs = new List<AudioDeviceSnapshot>();
        List<AudioDeviceSnapshot>? currentSection = null;
        var inAudioSection = false;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var sectionName = line.Trim();
            if (sectionName.Equals("Audio", StringComparison.Ordinal))
            {
                currentSection = null;
                inAudioSection = true;
                continue;
            }

            if (sectionName.Equals("Video", StringComparison.Ordinal))
            {
                currentSection = null;
                inAudioSection = false;
                continue;
            }

            if (!inAudioSection)
            {
                continue;
            }

            if (line.Contains("Sinks:", StringComparison.Ordinal))
            {
                currentSection = outputs;
                continue;
            }

            if (line.Contains("Sources:", StringComparison.Ordinal))
            {
                currentSection = inputs;
                continue;
            }

            if (line.Contains("Streams:", StringComparison.Ordinal) ||
                line.Contains("Filters:", StringComparison.Ordinal))
            {
                currentSection = null;
                continue;
            }

            if (currentSection is null)
            {
                continue;
            }

            var match = DeviceLine().Match(line);
            if (!match.Success ||
                !double.TryParse(match.Groups["volume"].Value, NumberStyles.Float, CultureInfo.InvariantCulture,
                    out var volume))
            {
                continue;
            }

            currentSection.Add(new AudioDeviceSnapshot(
                match.Groups["id"].Value,
                match.Groups["name"].Value.Trim(),
                Math.Clamp((int)Math.Round(volume * 100), 0, 100),
                match.Groups["muted"].Success,
                match.Groups["default"].Success));
        }

        return new AudioSnapshot(true, outputs, inputs, false);
    }

    internal static bool ParseRecordingState(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            foreach (var pipeWireObject in document.RootElement.EnumerateArray())
            {
                if (!pipeWireObject.TryGetProperty("type", out var type) ||
                    !type.ValueEquals("PipeWire:Interface:Node") ||
                    !pipeWireObject.TryGetProperty("info", out var info) ||
                    !info.TryGetProperty("state", out var state) ||
                    !state.ValueEquals("running") ||
                    !info.TryGetProperty("props", out var properties) ||
                    !properties.TryGetProperty("media.class", out var mediaClass) ||
                    !mediaClass.ValueEquals("Stream/Input/Audio"))
                {
                    continue;
                }

                if (properties.TryGetProperty("application.name", out _) ||
                    properties.TryGetProperty("application.process.binary", out _))
                {
                    if (!IsHiddenOrMonitorStream(properties))
                    {
                        return true;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Treat partial or unavailable graph snapshots as no active recording.
        }

        return false;
    }

    private static bool IsHiddenOrMonitorStream(JsonElement properties) =>
        PropertyEquals(properties, "media.role", "Abstract") ||
        PropertyContains(properties, "application.id", "pavucontrol") ||
        PropertyContains(properties, "application.process.binary", "pavucontrol") ||
        PropertyContains(properties, "node.name", "peak detect") ||
        PropertyContains(properties, "media.name", "peak detect") ||
        PropertyContains(properties, "target.object", ".monitor") ||
        PropertyContains(properties, "node.target", ".monitor");

    private static bool PropertyEquals(JsonElement properties, string name, string value) =>
        properties.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        property.GetString()?.Equals(value, StringComparison.OrdinalIgnoreCase) == true;

    private static bool PropertyContains(JsonElement properties, string name, string value) =>
        properties.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        property.GetString()?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

    [GeneratedRegex(@"(?<default>\*)?\s*(?<id>\d+)\.\s+(?<name>.+?)\s+\[vol:\s*(?<volume>\d+(?:\.\d+)?)(?<muted>\s+MUTED)?\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();
}

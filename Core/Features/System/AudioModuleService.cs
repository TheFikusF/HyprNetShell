using System.Globalization;
using System.Text.RegularExpressions;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed partial class AudioModuleService : IBarDataService
{
    public async ValueTask UpdateAsync(BarStateBuilder state, CancellationToken cancellationToken)
    {
        var output = await CommandRunner.TryReadAsync(
            "wpctl",
            "status",
            TimeSpan.FromMilliseconds(900),
            cancellationToken);

        state.Audio = ParseStatus(output);
    }

    internal static Task SetDefaultAsync(string deviceId) =>
        CommandRunner.TryRunAsync(
            "wpctl",
            ["set-default", deviceId],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

    internal static Task SetVolumeAsync(string deviceId, int volume) =>
        CommandRunner.TryRunAsync(
            "wpctl",
            ["set-volume", deviceId, $"{Math.Clamp(volume, 0, 100)}%"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

    internal static Task SetMutedAsync(string deviceId, bool muted) =>
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

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
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

            if (line.Contains("Filters:", StringComparison.Ordinal) ||
                line.Contains("Streams:", StringComparison.Ordinal) ||
                line.Contains("Video", StringComparison.Ordinal))
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

        return new AudioSnapshot(true, outputs, inputs);
    }

    [GeneratedRegex(@"(?<default>\*)?\s*(?<id>\d+)\.\s+(?<name>.+?)\s+\[vol:\s*(?<volume>\d+(?:\.\d+)?)(?<muted>\s+MUTED)?\s*\]", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceLine();
}

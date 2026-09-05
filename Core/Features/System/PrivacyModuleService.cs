using System.Globalization;
using System.Text.Json;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed class PrivacyModuleService(AudioModuleService audioService) : IBarDataService
{
    private enum VideoSourceKind
    {
        Screen,
        Camera,
    }

    private sealed record VideoSource(
        int Id,
        VideoSourceKind Kind,
        bool IsRunning,
        IReadOnlySet<string> Aliases);
    private sealed record VideoConsumer(
        int Id,
        string Application,
        string? Target,
        VideoSourceKind? KindHint);

    private PrivacySnapshot _snapshot = PrivacySnapshot.Empty;

    public PrivacySnapshot Snapshot => Volatile.Read(ref _snapshot);

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        var graph = await CommandRunner.TryReadAsync(
            "pw-dump",
            "-N",
            TimeSpan.FromMilliseconds(900),
            cancellationToken);
        var snapshot = ParseState(graph);
        var directCameraApplications = FindDirectCameraApplications(cancellationToken);
        if (directCameraApplications.Count > 0)
        {
            snapshot = snapshot with
            {
                CameraApplications = MergeApplications(snapshot.CameraApplications, directCameraApplications),
            };
        }

        if (audioService.Snapshot.IsRecording && snapshot.MicrophoneApplications.Count == 0)
        {
            snapshot = snapshot with { MicrophoneApplications = ["Unknown application"] };
        }

        Volatile.Write(ref _snapshot, snapshot);
    }

    internal static PrivacySnapshot ParseState(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return PrivacySnapshot.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(output);
            var sources = new List<VideoSource>();
            var consumers = new List<VideoConsumer>();
            var microphones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var links = new HashSet<(int Output, int Input)>();

            foreach (var pipeWireObject in document.RootElement.EnumerateArray())
            {
                if (!pipeWireObject.TryGetProperty("type", out var type) ||
                    !pipeWireObject.TryGetProperty("info", out var info))
                {
                    continue;
                }

                if (type.ValueEquals("PipeWire:Interface:Link"))
                {
                    AddLink(info, links);
                    continue;
                }

                if (!type.ValueEquals("PipeWire:Interface:Node") ||
                    !info.TryGetProperty("props", out var properties) ||
                    !TryGetObjectId(pipeWireObject, out var id))
                {
                    continue;
                }

                var isRunning = IsRunning(info);
                if (PropertyEquals(properties, "media.class", "Stream/Input/Audio"))
                {
                    if (isRunning && !IsHiddenOrMonitorStream(properties))
                    {
                        microphones.Add(ApplicationName(properties));
                    }
                    continue;
                }

                if (PropertyEquals(properties, "media.class", "Stream/Input/Video"))
                {
                    if (isRunning)
                    {
                        var target = StringProperty(properties, "target.object") ??
                                     StringProperty(properties, "node.target");
                        consumers.Add(new VideoConsumer(
                            id,
                            ApplicationName(properties),
                            target,
                            ClassifyVideoConsumer(properties, target)));
                    }
                    continue;
                }

                if (!PropertyEquals(properties, "media.class", "Video/Source") &&
                    !PropertyEquals(properties, "media.class", "Stream/Output/Video"))
                {
                    continue;
                }

                var kind = ClassifyVideoSource(properties);
                if (kind is not null)
                {
                    sources.Add(new VideoSource(id, kind.Value, isRunning, SourceAliases(id, properties)));
                }
            }

            var screenApplications = ApplicationsFor(
                VideoSourceKind.Screen,
                sources,
                consumers,
                links);
            var cameraApplications = ApplicationsFor(
                VideoSourceKind.Camera,
                sources,
                consumers,
                links);

            return new PrivacySnapshot(
                screenApplications,
                [.. microphones.Order(StringComparer.OrdinalIgnoreCase)],
                cameraApplications);
        }
        catch (JsonException)
        {
            return PrivacySnapshot.Empty;
        }
    }

    private static IReadOnlyList<string> ApplicationsFor(
        VideoSourceKind kind,
        IReadOnlyList<VideoSource> sources,
        IReadOnlyList<VideoConsumer> consumers,
        IReadOnlySet<(int Output, int Input)> links)
    {
        var applications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasActiveSource = false;
        foreach (var source in sources.Where(source => source.Kind == kind))
        {
            var connectedConsumers = consumers.Where(consumer =>
                links.Contains((source.Id, consumer.Id)) ||
                links.Contains((consumer.Id, source.Id)) ||
                consumer.Target is not null && source.Aliases.Contains(consumer.Target)).ToArray();
            if (!source.IsRunning && connectedConsumers.Length == 0)
            {
                continue;
            }

            hasActiveSource = true;
            foreach (var consumer in connectedConsumers)
            {
                applications.Add(consumer.Application);
            }
        }

        foreach (var consumer in consumers.Where(consumer => consumer.KindHint == kind))
        {
            applications.Add(consumer.Application);
        }

        if (applications.Count == 0 && hasActiveSource)
        {
            applications.Add("Unknown application");
        }

        return [.. applications.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static VideoSourceKind? ClassifyVideoSource(JsonElement properties)
    {
        if (PropertyContains(properties, "device.api", "v4l2") ||
            PropertyContains(properties, "device.api", "libcamera") ||
            PropertyContains(properties, "api.v4l2.path", "/dev/video") ||
            PropertyContains(properties, "api.libcamera.path", "libcamera") ||
            PropertyContains(properties, "node.name", "v4l2") ||
            PropertyContains(properties, "node.name", "libcamera") ||
            PropertyEquals(properties, "media.role", "Camera") ||
            PropertyContains(properties, "media.name", "camera") ||
            PropertyContains(properties, "media.name", "webcam") ||
            PropertyContains(properties, "node.description", "camera") ||
            PropertyContains(properties, "device.product.name", "camera"))
        {
            return VideoSourceKind.Camera;
        }

        if (PropertyEquals(properties, "media.role", "Screen") ||
            PropertyContains(properties, "node.name", "screencast") ||
            PropertyContains(properties, "node.name", "screen cast") ||
            PropertyContains(properties, "node.name", "xdg-desktop-portal") ||
            PropertyContains(properties, "media.name", "screencast") ||
            PropertyContains(properties, "media.name", "screen cast") ||
            PropertyContains(properties, "media.name", "screen capture"))
        {
            return VideoSourceKind.Screen;
        }

        return null;
    }

    private static VideoSourceKind? ClassifyVideoConsumer(JsonElement properties, string? target)
    {
        var kind = ClassifyVideoSource(properties);
        if (kind is not null)
        {
            return kind;
        }

        if (ContainsAny(target, "v4l2", "libcamera", "/dev/video", "camera", "webcam"))
        {
            return VideoSourceKind.Camera;
        }

        if (ContainsAny(target, "screencast", "screen cast", "screen-cast", "xdg-desktop-portal"))
        {
            return VideoSourceKind.Screen;
        }

        return null;
    }

    private static bool ContainsAny(string? value, params ReadOnlySpan<string> candidates)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (value.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<string> SourceAliases(int id, JsonElement properties)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            id.ToString(CultureInfo.InvariantCulture),
        };
        AddStringProperty(properties, "object.serial", aliases);
        AddStringProperty(properties, "node.name", aliases);
        return aliases;
    }

    private static void AddLink(JsonElement info, ISet<(int Output, int Input)> links)
    {
        if (TryGetInt(info, "output-node-id", out var output) &&
            TryGetInt(info, "input-node-id", out var input))
        {
            links.Add((output, input));
            return;
        }

        if (info.TryGetProperty("props", out var properties) &&
            TryGetInt(properties, "link.output.node", out output) &&
            TryGetInt(properties, "link.input.node", out input))
        {
            links.Add((output, input));
        }
    }

    private static bool TryGetObjectId(JsonElement pipeWireObject, out int id) =>
        TryGetInt(pipeWireObject, "id", out id);

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(property.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    private static bool IsRunning(JsonElement info) =>
        info.TryGetProperty("state", out var state) && state.ValueEquals("running");

    private static IReadOnlyList<string> FindDirectCameraApplications(CancellationToken cancellationToken)
    {
        var applications = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var processDirectory in Directory.EnumerateDirectories("/proc"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processId = Path.GetFileName(processDirectory);
                if (!int.TryParse(processId, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    continue;
                }

                try
                {
                    var descriptors = Path.Combine(processDirectory, "fd");
                    var usesCamera = Directory.EnumerateFileSystemEntries(descriptors).Any(descriptor =>
                        new FileInfo(descriptor).LinkTarget?.StartsWith("/dev/video", StringComparison.Ordinal) == true);
                    if (usesCamera)
                    {
                        applications.Add(ProcessName(processDirectory));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Processes can exit or revoke access while /proc is being scanned.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // /proc may be restricted by hidepid or a container policy.
        }

        return [.. applications.Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static string ProcessName(string processDirectory)
    {
        try
        {
            var executable = new FileInfo(Path.Combine(processDirectory, "exe")).LinkTarget;
            if (!string.IsNullOrWhiteSpace(executable))
            {
                return Path.GetFileName(executable);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Fall through to the kernel process name.
        }

        try
        {
            return File.ReadAllText(Path.Combine(processDirectory, "comm")).Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return "Unknown application";
        }
    }

    private static IReadOnlyList<string> MergeApplications(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second) =>
        [.. first.Concat(second).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    private static string ApplicationName(JsonElement properties) =>
        StringProperty(properties, "application.name") ??
        StringProperty(properties, "application.process.binary") ??
        StringProperty(properties, "application.id") ??
        StringProperty(properties, "media.name") ??
        StringProperty(properties, "node.name") ??
        "Unknown application";

    private static bool IsHiddenOrMonitorStream(JsonElement properties) =>
        PropertyEquals(properties, "media.role", "Abstract") ||
        PropertyContains(properties, "application.id", "pavucontrol") ||
        PropertyContains(properties, "application.process.binary", "pavucontrol") ||
        PropertyContains(properties, "node.name", "peak detect") ||
        PropertyContains(properties, "media.name", "peak detect") ||
        PropertyContains(properties, "target.object", ".monitor") ||
        PropertyContains(properties, "node.target", ".monitor");

    private static void AddStringProperty(JsonElement properties, string name, ISet<string> values)
    {
        var value = StringProperty(properties, name);
        if (value is not null)
        {
            values.Add(value);
        }
    }

    private static string? StringProperty(JsonElement properties, string name)
    {
        if (!properties.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null,
        };
    }

    private static bool PropertyEquals(JsonElement properties, string name, string value) =>
        StringProperty(properties, name)?.Equals(value, StringComparison.OrdinalIgnoreCase) == true;

    private static bool PropertyContains(JsonElement properties, string name, string value) =>
        StringProperty(properties, name)?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
}

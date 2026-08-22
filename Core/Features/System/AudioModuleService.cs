using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;
using HyprNetShell.Core.Platform;
using HyprNetShell.Core.Services;

namespace HyprNetShell.Core.Features.System;

internal sealed partial class AudioModuleService : IBarDataService, IDisposable
{
    private static readonly TimeSpan FallbackRefreshInterval = TimeSpan.FromSeconds(5);

    private readonly object _nativeGate = new();
    private readonly AudioNativeMethods.SnapshotCallback _snapshotCallback;
    private AudioSnapshot _snapshot = AudioSnapshot.Empty;
    private IntPtr _nativeHandle;
    private DateTime _nextFallbackRefreshUtc = DateTime.MinValue;
    private volatile bool _disposed;

    public AudioSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public AudioModuleService()
    {
        _snapshotCallback = OnNativeSnapshot;
        TryInitializeNativeBackend();
    }

    public async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (NativeAvailable() || !BeginFallbackRefresh())
        {
            return;
        }

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
        var fallbackSnapshot = ParseStatus(await statusTask) with
        {
            IsRecording = ParseRecordingState(await graphTask),
        };
        if (!NativeAvailable())
        {
            Volatile.Write(ref _snapshot, fallbackSnapshot);
        }
    }

    internal Task SetDefaultAsync(string deviceId)
    {
        if (TryNativeControl(deviceId, static (handle, id) =>
                AudioNativeMethods.hypr_audio_set_default(handle, id)))
        {
            return Task.CompletedTask;
        }

        return CommandRunner.TryRunAsync(
            "wpctl",
            ["set-default", deviceId],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
    }

    internal Task SetVolumeAsync(string deviceId, int volume)
    {
        var clampedVolume = Math.Clamp(volume, 0, 100);
        if (TryNativeControl(deviceId, (handle, id) =>
                AudioNativeMethods.hypr_audio_set_volume(handle, id, clampedVolume)))
        {
            return Task.CompletedTask;
        }

        return CommandRunner.TryRunAsync(
            "wpctl",
            ["set-volume", deviceId, $"{clampedVolume}%"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
    }

    internal Task SetMutedAsync(string deviceId, bool muted)
    {
        if (TryNativeControl(deviceId, (handle, id) =>
                AudioNativeMethods.hypr_audio_set_muted(handle, id, muted ? 1 : 0)))
        {
            return Task.CompletedTask;
        }

        return CommandRunner.TryRunAsync(
            "wpctl",
            ["set-mute", deviceId, muted ? "1" : "0"],
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
    }

    public void Dispose()
    {
        lock (_nativeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_nativeHandle != IntPtr.Zero)
            {
                AudioNativeMethods.hypr_audio_destroy(_nativeHandle);
                _nativeHandle = IntPtr.Zero;
            }
        }
        GC.KeepAlive(_snapshotCallback);
    }

    private void TryInitializeNativeBackend()
    {
        try
        {
            if (AudioNativeMethods.hypr_audio_get_abi_version() != AudioNativeMethods.AbiVersion)
            {
                AppLogger.Warning("Audio", "Native audio backend ABI version does not match; using wpctl fallback");
                return;
            }

            var callback = Marshal.GetFunctionPointerForDelegate(_snapshotCallback);
            _nativeHandle = AudioNativeMethods.hypr_audio_create(callback, IntPtr.Zero);
            if (_nativeHandle == IntPtr.Zero)
            {
                AppLogger.Warning("Audio", "Could not create native audio backend; using wpctl fallback");
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            AppLogger.Warning("Audio", "Native audio backend is not integrated; using wpctl fallback", exception);
            _nativeHandle = IntPtr.Zero;
        }
    }

    private bool NativeAvailable()
    {
        lock (_nativeGate)
        {
            return !_disposed &&
                   _nativeHandle != IntPtr.Zero &&
                   AudioNativeMethods.hypr_audio_is_available(_nativeHandle) != 0;
        }
    }

    private bool BeginFallbackRefresh()
    {
        lock (_nativeGate)
        {
            var now = DateTime.UtcNow;
            if (_disposed || now < _nextFallbackRefreshUtc)
            {
                return false;
            }

            _nextFallbackRefreshUtc = now + FallbackRefreshInterval;
            return true;
        }
    }

    private bool TryNativeControl(string deviceId, Func<IntPtr, uint, int> operation)
    {
        if (!uint.TryParse(deviceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id == 0)
        {
            return false;
        }

        lock (_nativeGate)
        {
            return !_disposed &&
                   _nativeHandle != IntPtr.Zero &&
                   AudioNativeMethods.hypr_audio_is_available(_nativeHandle) != 0 &&
                   operation(_nativeHandle, id) != 0;
        }
    }

    private void OnNativeSnapshot(IntPtr userData, IntPtr snapshotPointer)
    {
        _ = userData;
        try
        {
            if (_disposed || snapshotPointer == IntPtr.Zero)
            {
                return;
            }

            var native = Marshal.PtrToStructure<AudioNativeMethods.Snapshot>(snapshotPointer);
            if (native.AbiVersion != AudioNativeMethods.AbiVersion ||
                native.StructSize < Marshal.SizeOf<AudioNativeMethods.Snapshot>())
            {
                return;
            }

            var outputs = ReadDevices(native.Outputs, native.OutputCount);
            var inputs = ReadDevices(native.Inputs, native.InputCount);
            Volatile.Write(
                ref _snapshot,
                new AudioSnapshot(true, outputs, inputs, native.IsRecording != 0));
        }
        catch (Exception exception)
        {
            AppLogger.Warning("Audio", "Could not read a native audio snapshot", exception);
        }
    }

    private static IReadOnlyList<AudioDeviceSnapshot> ReadDevices(IntPtr pointer, uint count)
    {
        if (pointer == IntPtr.Zero || count == 0)
        {
            return Array.Empty<AudioDeviceSnapshot>();
        }

        var deviceSize = Marshal.SizeOf<AudioNativeMethods.Device>();
        var devices = new AudioDeviceSnapshot[checked((int)count)];
        for (var index = 0; index < devices.Length; index++)
        {
            var native = Marshal.PtrToStructure<AudioNativeMethods.Device>(
                IntPtr.Add(pointer, checked(index * deviceSize)));
            devices[index] = new AudioDeviceSnapshot(
                native.Id.ToString(CultureInfo.InvariantCulture),
                Marshal.PtrToStringUTF8(native.Name) ?? "Unknown audio device",
                Math.Clamp(native.Volume, 0, 100),
                native.Muted != 0,
                native.Active != 0);
        }
        return Array.AsReadOnly(devices);
    }

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

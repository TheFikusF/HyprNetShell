using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HyprNetShell.Core.Logging;
using HyprNetShell.Core.Models;

namespace HyprNetShell.Core.Features.System;

internal sealed class PipeWireGraphService : IDisposable, IAsyncDisposable
{
    private const string Category = "PipeWireGraph";
    private const int MaxFrameCharacters = 16 * 1024 * 1024;
    private readonly CancellationTokenSource _lifetime;
    private readonly Task _monitor;
    private readonly Lock _disposeGate = new();
    private Task? _disposal;
    private sealed record State(PrivacySnapshot Privacy, bool IsRecording);
    private State _state = new(PrivacySnapshot.Empty, false);

    public PrivacySnapshot Privacy => Volatile.Read(ref _state).Privacy;
    public bool IsRecording => Volatile.Read(ref _state).IsRecording;

    public PipeWireGraphService(CancellationToken cancellationToken = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitor = Task.Run(() => MonitorAsync(_lifetime.Token));
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        var backoffSeconds = 1;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var started = Stopwatch.GetTimestamp();
                try
                {
                    await RunMonitorAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    AppLogger.Warning(Category, "pw-dump monitor failed; reconnecting", exception);
                }

                Volatile.Write(ref _state, new State(PrivacySnapshot.Empty, false));
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                {
                    backoffSeconds = 1;
                }
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
                backoffSeconds = Math.Min(backoffSeconds * 2, 30);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            Volatile.Write(ref _state, new State(PrivacySnapshot.Empty, false));
        }
    }

    private async Task RunMonitorAsync(CancellationToken cancellationToken)
    {
        using var process = new Process {
            // pw-dump does not flush every monitor event when stdout is a pipe.
            // Line buffering keeps small state/removal updates from waiting for a full buffer.
            StartInfo = new ProcessStartInfo("stdbuf") {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-oL", "-eL", "pw-dump", "-N", "-m" },
            },
        };
        if (!process.Start())
        {
            throw new IOException("Could not start stdbuf -oL -eL pw-dump -N -m (requires GNU coreutils).");
        }

        using var reads = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stderr = DrainStderrAsync(process.StandardError, reads.Token);
        try
        {
            var graph = new Dictionary<int, JsonObject>();
            await ReadGraphAsync(process.StandardOutput, graph, reads.Token).ConfigureAwait(false);
            throw new IOException("pw-dump monitor closed stdout unexpectedly.");
        }
        finally
        {
            reads.Cancel();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process can exit between the state check and Kill.
            }
            catch (Exception exception)
            {
                AppLogger.Warning(Category, "Could not terminate pw-dump monitor", exception);
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                AppLogger.Warning(Category, "Could not reap pw-dump monitor within five seconds", exception);
            }
            await stderr.ConfigureAwait(false);
        }
    }

    private async Task ReadGraphAsync(
        StreamReader reader,
        Dictionary<int, JsonObject> graph,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var frame = new StringBuilder();
        var nesting = new Stack<char>();
        var inString = false;
        var escaped = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            for (var index = 0; index < count; index++)
            {
                var character = buffer[index];
                if (nesting.Count == 0)
                {
                    if (char.IsWhiteSpace(character))
                    {
                        continue;
                    }
                    if (character != '[')
                    {
                        throw new JsonException("Expected a pw-dump JSON array.");
                    }
                }

                frame.Append(character);
                if (frame.Length > MaxFrameCharacters)
                {
                    throw new JsonException("pw-dump JSON array exceeds the 16 Mi-character limit.");
                }
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (character == '\\')
                    {
                        escaped = true;
                    }
                    else if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (character == '"')
                {
                    inString = true;
                }
                else if (character is '[' or '{')
                {
                    nesting.Push(character);
                }
                else if (character is ']' or '}')
                {
                    if (!nesting.TryPop(out var opening) ||
                        (character == ']' ? opening != '[' : opening != '{'))
                    {
                        throw new JsonException("Mismatched delimiters in pw-dump JSON.");
                    }
                    if (nesting.Count == 0)
                    {
                        ApplyUpdates(frame.ToString(), graph);
                        frame.Clear();
                    }
                }
            }
        }
        if (frame.Length != 0)
        {
            throw new JsonException("pw-dump closed stdout during a JSON array.");
        }
    }

    private void ApplyUpdates(string frame, Dictionary<int, JsonObject> graph)
    {
        var updates = JsonNode.Parse(frame) as JsonArray
            ?? throw new JsonException("Expected a pw-dump JSON array.");
        foreach (var node in updates)
        {
            if (node is not JsonObject update || update["id"] is not JsonValue idValue ||
                !idValue.TryGetValue<int>(out var id))
            {
                throw new JsonException("pw-dump update has no integer object id.");
            }
            if (update.TryGetPropertyValue("info", out var info) && info is null)
            {
                graph.Remove(id);
                continue;
            }
            if (!graph.TryGetValue(id, out var current))
            {
                current = new JsonObject();
                graph.Add(id, current);
            }
            Merge(current, update);
        }

        var reconstructed = new JsonArray();
        foreach (var entry in graph.Values)
        {
            reconstructed.Add(entry.DeepClone());
        }
        var json = reconstructed.ToJsonString();
        Volatile.Write(ref _state, new State(
            PrivacyModuleService.ParseState(json),
            AudioModuleService.ParseRecordingState(json)));
    }

    private static void Merge(JsonObject target, JsonObject update)
    {
        foreach (var property in update)
        {
            if (property.Value is null)
            {
                target.Remove(property.Key);
            }
            else if (property.Value is JsonObject partial)
            {
                if (target[property.Key] is not JsonObject nested)
                {
                    nested = new JsonObject();
                    target[property.Key] = nested;
                }
                Merge(nested, partial);
            }
            else
            {
                target[property.Key] = property.Value.DeepClone();
            }
        }
    }

    private static async Task DrainStderrAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        var line = new StringBuilder();
        var window = Stopwatch.GetTimestamp();
        var logged = 0;
        void LogLine()
        {
            if (Stopwatch.GetElapsedTime(window) >= TimeSpan.FromMinutes(1))
            {
                window = Stopwatch.GetTimestamp();
                logged = 0;
            }
            if (line.Length > 0 && logged < 8)
            {
                AppLogger.Warning(Category, $"pw-dump stderr: {line}");
                logged++;
            }
            line.Clear();
        }

        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == '\n')
                    {
                        LogLine();
                    }
                    else if (buffer[index] != '\r' && line.Length < 512)
                    {
                        line.Append(buffer[index]);
                    }
                }
            }
            LogLine();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            AppLogger.Warning(Category, "Could not read pw-dump stderr", exception);
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _monitor.ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }
}

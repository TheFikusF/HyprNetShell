using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HyprNetShell.Core.Logging;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Features.System;

internal sealed class ClipboardHistoryService : IDisposable
{
    private const int MAX_ENTRY_BYTES = 64 * 1024 * 1024;
    private const int MAX_HISTORY_BYTES = 256 * 1024 * 1024;

    private static readonly string[] PreferredImageTypes =
    [
        "image/png", "image/jpeg", "image/webp", "image/avif", "image/gif", "image/bmp", "image/tiff",
    ];

    private static readonly string[] PreferredTextTypes =
    [
        "text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "TEXT", "STRING",
    ];

    private readonly Lock _gate = new();
    private readonly HistoryStore _history;
    private readonly List<ClipboardHistoryEntry> _entries = [];
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Task _watchTask;
    private Process? _watchProcess;
    private int _version;
    private long _lastChangeTimestamp;

    public int Version => Volatile.Read(ref _version);
    internal long LastChangeTimestamp => Interlocked.Read(ref _lastChangeTimestamp);

    public ClipboardHistoryService(HistoryStore history)
    {
        _history = history;
        _entries.AddRange(history.LoadClipboardEntries());
        _lastChangeTimestamp = _entries.Count == 0
            ? 0
            : new DateTimeOffset(_entries.Max(static entry => entry.CapturedAt), TimeSpan.Zero).ToUnixTimeMilliseconds();
        _history.LimitsChanged += ApplyHistoryLimit;
        _watchTask = Task.Run(() => WatchAsync(_disposeCancellation.Token));
    }

    public IReadOnlyList<ClipboardHistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public void Delete(ClipboardHistoryEntry entry)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(candidate =>
                candidate.Hash == entry.Hash &&
                candidate.MimeType.Equals(entry.MimeType, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            _history.DeleteClipboardEntry(_entries[index].MimeType, _entries[index].Hash);
            _entries.RemoveAt(index);
            Interlocked.Increment(ref _version);
        }
    }

    public void TogglePinned(ClipboardHistoryEntry entry)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(candidate =>
                candidate.Hash == entry.Hash &&
                candidate.MimeType.Equals(entry.MimeType, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            var updated = _entries[index] with {
                IsPinned = !_entries[index].IsPinned
            };
            _history.SetClipboardPinned(updated.MimeType, updated.Hash, updated.IsPinned);
            _entries[index] = updated;
            _entries.Sort(static (left, right) =>
            {
                var pinned = right.IsPinned.CompareTo(left.IsPinned);
                return pinned != 0 ? pinned : right.CapturedAt.CompareTo(left.CapturedAt);
            });
            Interlocked.Increment(ref _version);
        }
    }

    public async Task CopyAsync(ClipboardHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        if (!await TryWriteProcessAsync(
                "wl-copy",
                ["--type", entry.MimeType],
                entry.Data,
                cancellationToken) &&
            !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning("Clipboard", $"Could not copy clipboard history entry as {entry.MimeType}");
        }
    }

    public async Task<bool> CopyTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var data = Encoding.UTF8.GetBytes(text);
        var copied = await TryWriteProcessAsync(
                "wl-copy",
                ["--type", "text/plain;charset=utf-8"],
                data,
                cancellationToken) ||
            await TryWriteProcessAsync(
                "xclip",
                ["-selection", "clipboard"],
                data,
                cancellationToken);
        if (copied)
        {
            Interlocked.Exchange(ref _lastChangeTimestamp, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
        else if (!cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning("Clipboard", "Could not copy text with wl-copy or xclip");
        }

        return copied;
    }

    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken = default)
    {
        var text = await ReadProcessTextAsync(
            cancellationToken,
            "wl-paste",
            "--no-newline",
            "--type",
            "text");
        if (text is null && !cancellationToken.IsCancellationRequested)
        {
            AppLogger.Warning("Clipboard", "Could not read text with wl-paste");
        }

        return text;
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Process? process = null;
            try
            {
                await CaptureCurrentClipboardAsync(cancellationToken);
                process = Process.Start(CreateProcess("wl-paste", "--watch", "printf", "changed\n"));
                if (process is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                    continue;
                }

                lock (_gate)
                {
                    _watchProcess = process;
                }

                var drainErrors = process.StandardError.ReadToEndAsync(cancellationToken);
                while (await process.StandardOutput.ReadLineAsync(cancellationToken) is not null)
                {
                    await CaptureCurrentClipboardAsync(cancellationToken);
                }

                await process.WaitForExitAsync(cancellationToken);
                await drainErrors;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_watchProcess, process))
                    {
                        _watchProcess = null;
                    }
                }

                process?.Dispose();
            }
        }
    }

    private async Task CaptureCurrentClipboardAsync(CancellationToken cancellationToken)
    {
        var typeOutput = await ReadProcessTextAsync(cancellationToken, "wl-paste", "--list-types");
        if (string.IsNullOrWhiteSpace(typeOutput))
        {
            return;
        }

        var types = typeOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mimeType = SelectMimeType(types);
        if (mimeType is null)
        {
            return;
        }

        var data = await ReadProcessBytesAsync(
            cancellationToken,
            "wl-paste",
            "--no-newline",
            "--type",
            mimeType);
        if (data is not { Length: > 0 })
        {
            return;
        }

        AddEntry(mimeType, data);
    }

    private void AddEntry(string mimeType, byte[] data)
    {
        mimeType = mimeType.ToLowerInvariant();
        var isImage = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        var preview = isImage ? ImagePreview(mimeType, data.Length) : TextPreview(data);
        var image = isImage ? new EncodedImageData(mimeType, data) : null;
        var entry = new ClipboardHistoryEntry(0, mimeType, data, preview, image, hash, false, DateTime.UtcNow);

        lock (_gate)
        {
            var existingIndex = _entries.FindIndex(candidate =>
                candidate.Hash == hash && candidate.MimeType.Equals(mimeType, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                if (existingIndex == 0)
                {
                    Interlocked.Exchange(
                        ref _lastChangeTimestamp,
                        new DateTimeOffset(entry.CapturedAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
                    return;
                }

                entry = entry with {
                    Id = _entries[existingIndex].Id,
                    IsPinned = _entries[existingIndex].IsPinned,
                };
                _entries.RemoveAt(existingIndex);
            }

            var insertionIndex = entry.IsPinned ? 0 : _entries.FindLastIndex(candidate => candidate.IsPinned) + 1;
            _entries.Insert(insertionIndex, entry);
            Interlocked.Exchange(
                ref _lastChangeTimestamp,
                new DateTimeOffset(entry.CapturedAt, TimeSpan.Zero).ToUnixTimeMilliseconds());
            _history.SaveClipboardEntry(entry);
            var storedBytes = _entries.Sum(candidate => (long)candidate.Data.Length);
            while (_entries.Count > _history.ClipboardLimit || storedBytes > MAX_HISTORY_BYTES)
            {
                storedBytes -= _entries[^1].Data.Length;
                _entries.RemoveAt(_entries.Count - 1);
            }

            Interlocked.Increment(ref _version);
        }
    }

    private static string? SelectMimeType(IReadOnlyList<string> types)
    {
        foreach (var preferred in PreferredImageTypes)
        {
            var match = types.FirstOrDefault(type => type.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        var image = types.FirstOrDefault(type => type.StartsWith("image/", StringComparison.OrdinalIgnoreCase));
        if (image is not null)
        {
            return image;
        }

        foreach (var preferred in PreferredTextTypes)
        {
            var match = types.FirstOrDefault(type => type.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return types.FirstOrDefault(type => type.StartsWith("text/", StringComparison.OrdinalIgnoreCase));
    }

    private static string TextPreview(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data).Replace('\0', ' ');
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ImagePreview(string mimeType, int byteCount)
    {
        var format = mimeType["image/".Length..].ToUpperInvariant();
        var size = byteCount >= 1024 * 1024
            ? $"{byteCount / (1024.0 * 1024.0):0.#} MiB"
            : $"{Math.Max(1, byteCount / 1024.0):0.#} KiB";
        return $"Image · {format} · {size}";
    }

    private static async Task<bool> TryWriteProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        Process? process = null;
        try
        {
            process = Process.Start(CreateProcess(fileName, [.. arguments]));
            if (process is null)
            {
                return false;
            }

            var drainOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var drainErrors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.BaseStream.WriteAsync(data, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(drainOutput, drainErrors);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
        }
    }

    private static async Task<string?> ReadProcessTextAsync(
        CancellationToken cancellationToken,
        string fileName,
        params string[] arguments)
    {
        var data = await ReadProcessBytesAsync(cancellationToken, fileName, arguments);
        return data is null ? null : Encoding.UTF8.GetString(data);
    }

    private static async Task<byte[]?> ReadProcessBytesAsync(
        CancellationToken cancellationToken,
        string fileName,
        params string[] arguments)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        Process? process = null;
        try
        {
            process = Process.Start(CreateProcess(fileName, arguments));
            if (process is null)
            {
                return null;
            }

            var drainErrors = process.StandardError.ReadToEndAsync(timeout.Token);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MAX_ENTRY_BYTES)
                {
                    TryKill(process);
                    return null;
                }

                output.Write(buffer, 0, read);
            }

            await process.WaitForExitAsync(timeout.Token);
            await drainErrors;
            return process.ExitCode == 0 ? output.ToArray() : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (process is not null)
            {
                TryKill(process);
                process.Dispose();
            }
        }
    }

    private static ProcessStartInfo CreateProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill, or has already been
            // detached from this Process instance. Either way, cleanup is done.
        }
        catch (Exception exception)
        {
            AppLogger.Warning("ClipboardHistory", "Clipboard watcher did not stop cleanly", exception);
        }
    }

    private void ApplyHistoryLimit()
    {
        lock (_gate)
        {
            var storedBytes = _entries.Sum(candidate => (long)candidate.Data.Length);
            while (_entries.Count > _history.ClipboardLimit || storedBytes > MAX_HISTORY_BYTES)
            {
                storedBytes -= _entries[^1].Data.Length;
                _entries.RemoveAt(_entries.Count - 1);
            }

            Interlocked.Increment(ref _version);
        }
    }

    public void Dispose()
    {
        _history.LimitsChanged -= ApplyHistoryLimit;
        _disposeCancellation.Cancel();
        lock (_gate)
        {
            if (_watchProcess is not null)
            {
                TryKill(_watchProcess);
            }
        }

        try
        {
            _watchTask.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
        }
        _disposeCancellation.Dispose();
    }
}

internal sealed record ClipboardHistoryEntry(
    long Id,
    string MimeType,
    byte[] Data,
    string Preview,
    EncodedImageData? Image,
    string Hash,
    bool IsPinned,
    DateTime CapturedAt);

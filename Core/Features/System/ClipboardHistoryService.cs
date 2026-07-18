using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using HyprNetShell.Core.Logging;
using HyprNetShell.Rendering;

namespace HyprNetShell.Core.Features.System;

internal sealed class ClipboardHistoryService : IDisposable
{
    private const int MAX_ENTRIES = 100;
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
    private readonly List<ClipboardHistoryEntry> _entries = [];
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly Task _watchTask;
    private Process? _watchProcess;
    private int _version;

    public int Version => Volatile.Read(ref _version);

    public ClipboardHistoryService()
    {
        _watchTask = Task.Run(() => WatchAsync(_disposeCancellation.Token));
    }

    public IReadOnlyList<ClipboardHistoryEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }

    public async Task CopyAsync(ClipboardHistoryEntry entry)
    {
        Process? process = null;
        try
        {
            process = Process.Start(CreateProcess("wl-copy", "--type", entry.MimeType));
            if (process is null)
            {
                return;
            }

            await process.StandardInput.BaseStream.WriteAsync(entry.Data);
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception exception)
        {
            AppLogger.Warning("ClipboardHistory", "Could not terminate the clipboard watcher", exception);
            // Clipboard transport is optional; history remains available if copying fails.
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
        var isImage = mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
        var hash = Convert.ToHexString(SHA256.HashData(data));
        var preview = isImage ? ImagePreview(mimeType, data.Length) : TextPreview(data);
        var image = isImage ? new EncodedImageData(mimeType, data) : null;
        var entry = new ClipboardHistoryEntry(mimeType, data, preview, image, hash);

        lock (_gate)
        {
            var existingIndex = _entries.FindIndex(candidate =>
                candidate.Hash == hash && candidate.MimeType.Equals(mimeType, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                if (existingIndex == 0)
                {
                    return;
                }

                entry = _entries[existingIndex];
                _entries.RemoveAt(existingIndex);
            }

            _entries.Insert(0, entry);
            var storedBytes = _entries.Sum(candidate => (long)candidate.Data.Length);
            while (_entries.Count > MAX_ENTRIES || storedBytes > MAX_HISTORY_BYTES)
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
        var startInfo = new ProcessStartInfo
        {
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

    public void Dispose()
    {
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
    string MimeType,
    byte[] Data,
    string Preview,
    EncodedImageData? Image,
    string Hash);

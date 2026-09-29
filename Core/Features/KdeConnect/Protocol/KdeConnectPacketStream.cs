using System.Buffers;
using System.Text;

namespace HyprNetShell.Core.Features.KdeConnect.Protocol;

internal sealed class KdeConnectPacketStream : IDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    internal KdeConnectPacketStream(Stream stream) => _stream = stream;

    internal async Task<KdeConnectPacket> ReadAsync(
        CancellationToken cancellationToken,
        int maximumBytes = KdeConnectProtocol.MaximumPacketBytes)
    {
        if (maximumBytes <= 0 || maximumBytes > KdeConnectProtocol.MaximumPacketBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                var length = newline - _start;
                if (length > maximumBytes)
                {
                    throw new InvalidDataException("KDE Connect packet exceeds the size limit.");
                }
                if (length == 0)
                {
                    _start = newline + 1;
                    continue;
                }

                if (!KdeConnectProtocol.TryParse(_buffer.AsSpan(_start, length), out var packet))
                {
                    throw new InvalidDataException("Received malformed KDE Connect JSON.");
                }

                _start = newline + 1;
                CompactIfNeeded();
                return packet!;
            }

            if (_end - _start >= maximumBytes)
            {
                throw new InvalidDataException("KDE Connect packet exceeds the size limit.");
            }

            EnsureCapacity();
            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("KDE Connect peer closed the connection.");
            }
            _end += read;
        }
    }

    internal async Task WriteAsync(ReadOnlyMemory<byte> framedPacket, CancellationToken cancellationToken)
    {
        if (framedPacket.Length > KdeConnectProtocol.MaximumPacketBytes)
        {
            throw new InvalidDataException("KDE Connect packet exceeds the size limit.");
        }
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await _stream.WriteAsync(framedPacket, cancellationToken);
            await _stream.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void EnsureCapacity()
    {
        if (_end < _buffer.Length)
        {
            return;
        }

        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
            return;
        }

        var nextSize = Math.Min(_buffer.Length * 2, KdeConnectProtocol.MaximumPacketBytes);
        Array.Resize(ref _buffer, nextSize);
    }

    private void CompactIfNeeded()
    {
        if (_start == _end)
        {
            _start = 0;
            _end = 0;
        }
        else if (_start > _buffer.Length / 2)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }
    }

    public void Dispose() => _stream.Dispose();
}

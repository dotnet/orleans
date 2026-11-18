using System.Buffers;

namespace Orleans.Journaling;

internal sealed class ReadOnlySequenceStream(ReadOnlySequence<byte> sequence) : Stream
{
    private readonly ReadOnlySequence<byte> _sequence = sequence;
    private long _position;
    private bool _disposed;

    public override bool CanRead => !_disposed;

    public override bool CanSeek => !_disposed;

    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _sequence.Length;
        }
    }

    public override long Position
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _position;
        }

        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (value < 0 || value > _sequence.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _position = value;
        }
    }

    public override void Flush() => ObjectDisposedException.ThrowIf(_disposed, this);

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (buffer.IsEmpty || _position >= _sequence.Length)
        {
            return 0;
        }

        var length = (int)Math.Min(buffer.Length, _sequence.Length - _position);
        _sequence.Slice(_position, length).CopyTo(buffer);
        _position += length;
        return length;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<int>(Read(buffer.Span));
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var newPosition = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _sequence.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        Position = newPosition;
        return _position;
    }

    public override void SetLength(long value) => throw new NotSupportedException("This stream is read-only.");

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("This stream is read-only.");

    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException("This stream is read-only.");

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }
}

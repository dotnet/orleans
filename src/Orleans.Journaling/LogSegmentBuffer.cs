using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed class LogSegmentBuffer : IDisposable, ILogEntryWriterTarget, ILogSegmentWriter, ILogWriterTarget
{
    private const int LengthPrefixSize = sizeof(uint);
    private readonly ArcBufferWriter _buffer = new();
    private readonly LogEntryWriter _entryWriter = new();

    public int Length => _buffer.Length;

    long ILogSegmentWriter.Length => _buffer.Length;

    public bool IsEmpty => _buffer.Length == 0;

    public LogWriter CreateLogWriter(LogStreamId streamId) => new(streamId, this);

    public void Advance(int count) => _buffer.AdvanceWriter(count);

    public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => _buffer.GetSpan(sizeHint);

    public void Write(ReadOnlySpan<byte> value) => _buffer.Write(value);

    public void Write(ReadOnlySequence<byte> value) => _buffer.Write(value);

    public void WriteUInt32LittleEndianAt(int offset, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        _buffer.WriteAt(offset, bytes);
    }

    public LogEntryWriter BeginEntry(LogStreamId streamId, ILogEntryWriterCompletion? completion = null)
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The log segment already has an active entry.");
        }

        var entryStart = Length;
        Span<byte> lengthPrefix = stackalloc byte[LengthPrefixSize];
        Write(lengthPrefix);
        VarIntHelper.WriteVarUInt64(this, streamId.Value);
        _entryWriter.Initialize(this, entryStart, completion);
        return _entryWriter;
    }

    public void Truncate(int length) => _buffer.Truncate(length);

    public void CommitEntry(int entryStart)
    {
        var length = checked((uint)(Length - entryStart - LengthPrefixSize));
        WriteUInt32LittleEndianAt(entryStart, length);
    }

    public void AbortEntry(int entryStart) => Truncate(entryStart);

    public ArcBuffer PeekSlice() => _buffer.PeekSlice(_buffer.Length);

    public ArcBuffer GetCommittedBuffer()
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The log segment has an active entry.");
        }

        return _buffer.PeekSlice(_buffer.Length);
    }

    public ReadOnlySequence<byte> AsReadOnlySequence()
    {
        using var buffer = PeekSlice();
        return buffer.AsReadOnlySequence();
    }

    public Stream AsReadOnlyStream() => new ReadOnlyStream(PeekSlice());

    public void Reset()
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The log segment cannot be reset while an entry is active.");
        }

        _buffer.Reset();
    }

    public void Dispose() => _buffer.Dispose();

    LogEntryWriter ILogWriterTarget.BeginEntry(LogStreamId streamId, ILogEntryWriterCompletion? completion) => BeginEntry(streamId, completion);

    private sealed class ReadOnlyStream(ArcBuffer buffer) : Stream
    {
        private ArcBuffer _buffer = buffer;
        private readonly ReadOnlySequence<byte> _sequence = buffer.AsReadOnlySequence();
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;

        public override bool CanSeek => !_disposed;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _sequence.Length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0 || value > _sequence.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            ThrowIfDisposed();

            if (buffer.IsEmpty)
            {
                return 0;
            }

            var remaining = _sequence.Length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, remaining);
            _sequence.Slice(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();

            var position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _sequence.Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };

            Position = position;
            return _position;
        }

        public override void SetLength(long value) => throw GetReadOnlyException();

        public override void Write(byte[] buffer, int offset, int count) => throw GetReadOnlyException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw GetReadOnlyException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _buffer.Dispose();
                _disposed = true;
            }

            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(ReadOnlyStream));
            }
        }

        private static NotSupportedException GetReadOnlyException() => new("This stream is read-only.");
    }
}

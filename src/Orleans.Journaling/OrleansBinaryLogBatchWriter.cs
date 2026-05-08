using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal sealed class OrleansBinaryLogBatchWriter : IDisposable, ILogEntryWriterTarget, ILogBatchWriter, ILogStreamWriterTarget
{
    private readonly ArcBufferWriter _buffer = new();
    private readonly ArcBufferWriter _entryBuffer = new();
    private readonly LogEntryWriter _entryWriter = new();

    public int Length
    {
        get
        {
            var length = _buffer.Length;
            if (_entryWriter.IsActive)
            {
                var bodyLength = checked((uint)_entryBuffer.Length);
                length = checked(length + VarIntHelper.GetVarUInt32ByteCount(bodyLength) + _entryBuffer.Length);
            }

            return length;
        }
    }

    long ILogBatchWriter.Length => Length;

    public bool IsEmpty => Length == 0;

    public LogStreamWriter CreateLogStreamWriter(LogStreamId streamId) => new(streamId, this);

    public void Advance(int count) => GetWriteBuffer().AdvanceWriter(count);

    public Memory<byte> GetMemory(int sizeHint = 0) => GetWriteBuffer().GetMemory(sizeHint);

    public Span<byte> GetSpan(int sizeHint = 0) => GetWriteBuffer().GetSpan(sizeHint);

    public void Write(ReadOnlySpan<byte> value) => GetWriteBuffer().Write(value);

    public void Write(ReadOnlySequence<byte> value) => GetWriteBuffer().Write(value);

    public LogEntryWriter BeginEntry(LogStreamId streamId, ILogEntryWriterCompletion? completion = null)
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The log batch already has an active entry.");
        }

        var entryStart = Length;
        _entryBuffer.Reset();
        VarIntHelper.WriteVarUInt64(_entryBuffer, streamId.Value);
        _entryWriter.Initialize(this, entryStart, completion);
        return _entryWriter;
    }

    public void CommitEntry(int entryStart)
    {
        ValidateEntryStart(entryStart);
        var length = checked((uint)_entryBuffer.Length);
        VarIntHelper.WriteVarUInt32(_buffer, length);
        using var body = _entryBuffer.PeekSlice(_entryBuffer.Length);
        _buffer.Write(body.AsReadOnlySequence());
        _entryBuffer.Reset();
    }

    public void AbortEntry(int entryStart)
    {
        ValidateEntryStart(entryStart);
        _entryBuffer.Reset();
    }

    public ArcBuffer PeekSlice() => _buffer.PeekSlice(_buffer.Length);

    public ArcBuffer GetCommittedBuffer()
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The log batch has an active entry.");
        }

        return _buffer.PeekSlice(_buffer.Length);
    }

    public Stream AsReadOnlyStream() => new ReadOnlyStream(PeekSlice());

    public void Reset()
    {
        if (_entryWriter.IsActive)
        {
            throw new InvalidOperationException("The log batch cannot be reset while an entry is active.");
        }

        _buffer.Reset();
        _entryBuffer.Reset();
    }

    public void Dispose()
    {
        _entryBuffer.Dispose();
        _buffer.Dispose();
    }

    LogEntryWriter ILogStreamWriterTarget.BeginEntry(LogStreamId streamId, ILogEntryWriterCompletion? completion) => BeginEntry(streamId, completion);

    void ILogStreamWriterTarget.AppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry is not OrleansBinaryFormattedLogEntry binaryEntry)
        {
            throw new InvalidOperationException(
                $"The Orleans binary log batch writer cannot append formatted entry of type '{entry.GetType().FullName}'.");
        }

        using var logEntry = new LogEntry(BeginEntry(streamId));
        logEntry.Writer.Write(binaryEntry.Payload.Span);
        logEntry.Commit();
    }

    bool ILogStreamWriterTarget.TryAppendFormattedEntry(LogStreamId streamId, IFormattedLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry is not OrleansBinaryFormattedLogEntry binaryEntry)
        {
            return false;
        }

        using var logEntry = new LogEntry(BeginEntry(streamId));
        logEntry.Writer.Write(binaryEntry.Payload.Span);
        logEntry.Commit();
        return true;
    }

    private ArcBufferWriter GetWriteBuffer() => _entryWriter.IsActive ? _entryBuffer : _buffer;

    private void ValidateEntryStart(int entryStart)
    {
        if (entryStart != _buffer.Length)
        {
            throw new InvalidOperationException("The log entry start does not match the active entry.");
        }
    }

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

using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal class OrleansBinaryJournalWriter : JournalWriter
{
    private readonly ArcBufferWriter _buffer = new();
    private readonly ArcBufferWriter _entryBuffer = new();
    private int _activeEntryStart;

    public int Length => checked(_buffer.Length + _entryBuffer.Length);

    protected override ArcBuffer GetCommittedBufferCore() => _buffer.PeekSlice(_buffer.Length);

    protected override void ResetCore()
    {
        _activeEntryStart = 0;
        _buffer.Reset();
        _entryBuffer.Reset();
    }

    protected override IBufferWriter<byte> BeginEntryCore(JournalStreamId streamId)
    {
        _activeEntryStart = _buffer.Length;
        _entryBuffer.Reset();
        var streamIdWriter = Writer.Create(_entryBuffer, session: null!);
        streamIdWriter.WriteVarUInt64(streamId.Value);
        streamIdWriter.Commit();
        return _entryBuffer;
    }

    protected override void CommitEntry(JournalStreamId streamId)
    {
        ValidateEntryStart();
        var length = checked((uint)_entryBuffer.Length);
        var lengthWriter = Writer.Create(_buffer, session: null!);
        lengthWriter.WriteVarUInt32(length);
        lengthWriter.Commit();
        using var body = _entryBuffer.PeekSlice(_entryBuffer.Length);
        _buffer.Write(body.AsReadOnlySequence());
        _activeEntryStart = 0;
        _entryBuffer.Reset();
    }

    protected override void AbortEntry(JournalStreamId streamId)
    {
        ValidateEntryStart();
        _activeEntryStart = 0;
        _entryBuffer.Reset();
    }

    public ArcBuffer Peek() => _buffer.PeekSlice(_buffer.Length);

    /// <summary>
    /// Returns a read-only stream over a pinned snapshot of the current committed bytes.
    /// </summary>
    public Stream AsReadOnlyStream() => new ReadOnlyStream(Peek());

    public override void Dispose()
    {
        _entryBuffer.Dispose();
        _buffer.Dispose();
    }

    protected override void OnAppendPreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
    {
        if (!string.Equals(entry.FormatKey, OrleansBinaryJournalFormat.JournalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Orleans binary journal writer cannot append preserved entry for journal format key '{entry.FormatKey}'.");
            }

        using var journalEntry = CreateJournalStreamWriter(streamId).BeginEntry();
        journalEntry.PayloadWriter.Write(entry.Payload.Span);
        journalEntry.Commit();
    }

    private void ValidateEntryStart()
    {
        if (_activeEntryStart != _buffer.Length)
        {
            throw new InvalidOperationException("The journal entry start does not match the active entry.");
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

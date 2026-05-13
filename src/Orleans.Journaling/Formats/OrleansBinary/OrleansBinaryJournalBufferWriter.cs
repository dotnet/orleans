using System.Buffers;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal class OrleansBinaryJournalBufferWriter : JournalBufferWriter
{
    public int Length => BufferedLength;

    protected override void WriteEntry(JournalStreamId streamId, ReadOnlySequence<byte> payload, IBufferWriter<byte> output)
    {
        var length = checked((uint)(GetVarUInt32ByteCount(streamId.Value) + payload.Length));
        var writer = Writer.Create(output, session: null!);
        writer.WriteVarUInt32(length);
        writer.WriteVarUInt32(streamId.Value);
        writer.Commit();
        WriteSequence(output, payload);
    }

    public ArcBuffer Peek() => GetCommittedBuffer();

    /// <summary>
    /// Returns a read-only stream over a pinned snapshot of the current committed bytes.
    /// </summary>
    public Stream AsReadOnlyStream() => new ReadOnlyStream(Peek());

    protected override void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry, IBufferWriter<byte> output)
    {
        if (!string.Equals(entry.FormatKey, OrleansBinaryJournalFormat.JournalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Orleans binary journal buffer writer cannot append preserved entry for journal format key '{entry.FormatKey}'.");
        }

        WriteEntry(streamId, new ReadOnlySequence<byte>(entry.Payload), output);
    }

    private static void WriteSequence(IBufferWriter<byte> output, ReadOnlySequence<byte> input)
    {
        foreach (var segment in input)
        {
            var span = segment.Span;
            while (!span.IsEmpty)
            {
                var destination = output.GetSpan(span.Length);
                var length = Math.Min(destination.Length, span.Length);
                span[..length].CopyTo(destination);
                output.Advance(length);
                span = span[length..];
            }
        }
    }

    private static int GetVarUInt32ByteCount(uint value) => value switch
    {
        < 128u => 1,
        < 16_384u => 2,
        < 2_097_152u => 3,
        < 268_435_456u => 4,
        _ => 5
    };

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

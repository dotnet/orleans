using System.Buffers;
using System.Buffers.Binary;
using Orleans.Serialization.Buffers;

namespace Orleans.Journaling;

internal class OrleansBinaryJournalBufferWriter : JournalBufferWriter
{
    private const int ByteCount = sizeof(byte);
    private const int UInt32ByteCount = sizeof(uint);
    private const int VersionedLengthPrefixLength = ByteCount + UInt32ByteCount;

    public int Length => BufferedLength;

    protected override void StartEntry(JournalStreamId streamId)
    {
        WriteByte(Output, OrleansBinaryJournalReader.FramingVersion);
        WriteUInt32(Output, 0);
        WriteUInt32(Output, streamId.Value);
    }

    protected override void FinishEntry(JournalStreamId streamId)
    {
        var length = checked((uint)(BufferedLength - ActiveEntryStart - VersionedLengthPrefixLength));
        Span<byte> encoded = stackalloc byte[UInt32ByteCount];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, length);
        WriteAt(ActiveEntryStart + ByteCount, encoded);
    }

    public ArcBuffer Peek() => GetCommittedBuffer();

    /// <summary>
    /// Returns a read-only stream over a pinned snapshot of the current committed bytes.
    /// </summary>
    public Stream AsReadOnlyStream() => new ReadOnlyStream(Peek());

    protected override void WritePreservedEntry(JournalStreamId streamId, IPreservedJournalEntry entry)
    {
        if (!string.Equals(entry.FormatKey, OrleansBinaryJournalFormat.JournalFormatKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The Orleans binary journal buffer writer cannot append preserved entry for journal format key '{entry.FormatKey}'.");
        }

        var entryStart = BufferedLength;
        WriteByte(Output, OrleansBinaryJournalReader.FramingVersion);
        WriteUInt32(Output, 0);
        WriteUInt32(Output, streamId.Value);
        WriteBytes(Output, entry.Payload.Span);

        var length = checked((uint)(BufferedLength - entryStart - VersionedLengthPrefixLength));
        Span<byte> encoded = stackalloc byte[UInt32ByteCount];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, length);
        WriteAt(entryStart + ByteCount, encoded);
    }

    private static void WriteBytes(IBufferWriter<byte> output, ReadOnlySpan<byte> input)
    {
        while (!input.IsEmpty)
        {
            var destination = output.GetSpan(input.Length);
            var length = Math.Min(destination.Length, input.Length);
            input[..length].CopyTo(destination);
            output.Advance(length);
            input = input[length..];
        }
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        var destination = output.GetSpan(ByteCount);
        destination[0] = value;
        output.Advance(ByteCount);
    }

    private static void WriteUInt32(IBufferWriter<byte> output, uint value)
    {
        var destination = output.GetSpan(UInt32ByteCount);
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        output.Advance(UInt32ByteCount);
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

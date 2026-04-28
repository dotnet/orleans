using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;

#nullable disable
namespace Orleans.Journaling;

/// <summary>
/// A mutable builder for creating log segments.
/// </summary>
public sealed partial class LogExtentBuilder(ArcBufferWriter buffer) : IDisposable, ILogEntryWriterTarget
{
    private readonly List<uint> _entryLengths = [];
    private readonly byte[] _scratch = new byte[8];
    private readonly ArcBufferWriter _buffer = buffer;
    private readonly LogEntryWriter _entryWriter = new();

    public LogExtentBuilder() : this(new())
    {
    }

    public long Length => _buffer.Length;

    public byte[] ToArray()
    {
        using var memoryStream = new PooledBufferStream();
        CopyTo(memoryStream, 4096);
        return memoryStream.ToArray();
    }

    public StateMachineStorageWriter CreateLogWriter(StateMachineId id) => new(id, this);

    public bool IsEmpty => _buffer.Length == 0;

    /// <summary>
    /// Gets the logical log entries currently collected in this extent.
    /// </summary>
    public IEnumerable<LogExtent.Entry> Entries => EntryEnumerator.Create(this);

    internal void AppendEntry(StateMachineId id, byte[] value) => AppendEntry(id, (ReadOnlySpan<byte>)value);
    internal void AppendEntry(StateMachineId id, Span<byte> value) => AppendEntry(id, (ReadOnlySpan<byte>)value);
    internal void AppendEntry(StateMachineId id, Memory<byte> value) => AppendEntry(id, value.Span);
    internal void AppendEntry(StateMachineId id, ReadOnlyMemory<byte> value) => AppendEntry(id, value.Span);
    internal void AppendEntry(StateMachineId id, ArraySegment<byte> value) => AppendEntry(id, value.AsSpan());
    internal void AppendEntry(StateMachineId id, ReadOnlySpan<byte> value)
    {
        var startOffset = _buffer.Length;
        var writer = Writer.Create(this, session: null);
        writer.WriteVarUInt64(id.Value);
        writer.Commit();

        _buffer.Write(value);

        var endOffset = _buffer.Length;
        _entryLengths.Add((uint)(endOffset - startOffset));
    }

    internal LogEntryWriter BeginEntry(StateMachineId id, ILogEntryWriterCompletion completion = null)
    {
        var startOffset = _buffer.Length;
        var writer = Writer.Create(this, session: null);
        writer.WriteVarUInt64(id.Value);
        writer.Commit();
        _entryWriter.Initialize(this, startOffset, completion);
        return _entryWriter;
    }

    internal void AppendEntry(StateMachineId id, ReadOnlySequence<byte> value)
    {
        var startOffset = _buffer.Length;

        var writer = Writer.Create(this, session: null);
        writer.WriteVarUInt64(id.Value);
        writer.Commit();

        _buffer.Write(value);

        var endOffset = _buffer.Length;
        _entryLengths.Add((uint)(endOffset - startOffset));
    }

    public void Reset()
    {
        _buffer.Reset();
        _entryLengths.Clear();
    }

    public void Dispose() => Reset();

    // Implemented on this class to prevent the need to repeatedly box & unbox _buffer.
    void IBufferWriter<byte>.Advance(int count) => _buffer.AdvanceWriter(count);
    Memory<byte> IBufferWriter<byte>.GetMemory(int sizeHint) => _buffer.GetMemory(sizeHint);
    Span<byte> IBufferWriter<byte>.GetSpan(int sizeHint) => _buffer.GetSpan(sizeHint);
    void ILogEntryWriterTarget.Write(ReadOnlySpan<byte> value) => _buffer.Write(value);
    void ILogEntryWriterTarget.CommitEntry(int entryStart) => _entryLengths.Add((uint)(_buffer.Length - entryStart));
    void ILogEntryWriterTarget.AbortEntry(int entryStart) => _buffer.Truncate(entryStart);

    public async ValueTask CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
    {
        using var buffer = _buffer.PeekSlice(_buffer.Length);
        var segments = buffer.MemorySegments;
        var currentSegment = ReadOnlyMemory<byte>.Empty;
        foreach (var entryLength in _entryLengths)
        {
            await destination.WriteAsync(GetLengthBytes(_scratch, entryLength), cancellationToken);

            var remainingEntryLength = entryLength;
            while (remainingEntryLength > 0)
            {
                // Move to the next memory segment if necessary.
                if (currentSegment.Length == 0)
                {
                    var hasNext = segments.MoveNext();
                    Debug.Assert(hasNext);
                    currentSegment = segments.Current;
                    continue;
                }

                var copyLen = Math.Min((uint)bufferSize, Math.Min(remainingEntryLength, (uint)currentSegment.Length));

                await destination.WriteAsync(currentSegment[..(int)copyLen], cancellationToken);

                remainingEntryLength -= copyLen;
                currentSegment = currentSegment[(int)copyLen..];
            }
        }
    }

    public void CopyTo(Stream destination, int bufferSize)
    {
        using var buffer = _buffer.PeekSlice(_buffer.Length);
        var segments = buffer.MemorySegments;
        var currentSegment = ReadOnlyMemory<byte>.Empty;
        foreach (var entryLength in _entryLengths)
        {
            destination.Write(GetLengthBytes(_scratch, entryLength).Span);

            var remainingEntryLength = entryLength;
            while (remainingEntryLength > 0)
            {
                // Move to the next memory segment if necessary.
                if (currentSegment.Length == 0)
                {
                    var hasNext = segments.MoveNext();
                    Debug.Assert(hasNext);
                    currentSegment = segments.Current;
                    continue;
                }

                var copyLen = Math.Min((uint)bufferSize, Math.Min(remainingEntryLength, (uint)currentSegment.Length));

                destination.Write(currentSegment[..(int)copyLen].Span);

                remainingEntryLength -= copyLen;
                currentSegment = currentSegment[(int)copyLen..];
            }
        }
    }

    private static ReadOnlyMemory<byte> GetLengthBytes(byte[] scratch, uint length)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, length);
        return new ReadOnlyMemory<byte>(scratch, 0, sizeof(uint));
    }

    private struct EntryEnumerator : IEnumerable<LogExtent.Entry>, IEnumerator<LogExtent.Entry>
    {
        private LogExtentBuilder _builder;
        private Orleans.Serialization.Buffers.ArcBuffer _buffer;
        private ReadOnlySequence<byte> _current;
        private int _index;
        private int _length;

        private EntryEnumerator(LogExtentBuilder builder)
        {
            _builder = builder;
            _buffer = builder._buffer.PeekSlice(builder._buffer.Length);
            _current = _buffer.AsReadOnlySequence();
            _index = 0;
            _length = -2;
        }

        public readonly EntryEnumerator GetEnumerator() => this;

        public static EntryEnumerator Create(LogExtentBuilder builder) => new(builder);

        public bool MoveNext()
        {
            if (_length == -1)
            {
                ThrowEnumerationNotStartedOrEnded();
            }

            if (_length >= 0)
            {
                _current = _current.Slice(_length);
            }

            if (_index >= _builder._entryLengths.Count)
            {
                _length = -1;
                _buffer.Dispose();
                return false;
            }

            _length = (int)_builder._entryLengths[_index++];
            return true;
        }

        public readonly LogExtent.Entry Current
        {
            get
            {
                if (_length < 0)
                {
                    ThrowEnumerationNotStartedOrEnded();
                }

                var slice = _current.Slice(0, _length);
                var reader = Reader.Create(slice, null);
                var id = reader.ReadVarUInt64();
                return new(new(id), slice.Slice(reader.Position));
            }
        }

        private readonly void ThrowEnumerationNotStartedOrEnded()
        {
            Debug.Assert(_length is (-1) or (-2));
            throw new InvalidOperationException(_length == -2 ? "Enumeration has not started." : "Enumeration has completed.");
        }

        readonly object IEnumerator.Current => Current;

        public void Reset()
        {
            _buffer.Dispose();
            this = new(_builder);
        }

        public void Dispose()
        {
            _length = -1;
            _buffer.Dispose();
        }

        readonly IEnumerator<LogExtent.Entry> IEnumerable<LogExtent.Entry>.GetEnumerator() => GetEnumerator();
        readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

using System.Buffers;
using System.Diagnostics;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Buffers.Adaptors;

namespace Orleans.Journaling;

/// <summary>
/// A mutable builder for creating log segments.
/// </summary>
public sealed partial class LogExtentBuilder(ArcBufferWriter buffer) : IDisposable, IBufferWriter<byte>
{
    private readonly List<uint> _entryLengths = [];
    private readonly byte[] _scratch = new byte[8];
    private readonly ArcBufferWriter _buffer = buffer;

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

    internal void AppendEntry<T>(StateMachineId id, Action<T, IBufferWriter<byte>> valueWriter, T value)
    {
        var startOffset = _buffer.Length;

        var writer = Writer.Create(this, session: null);
        writer.WriteVarUInt64(id.Value);
        writer.Commit();
        valueWriter(value, this);

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
        var writer = Writer.Create(scratch, null);
        writer.WriteVarUInt32(length);
        return new ReadOnlyMemory<byte>(scratch, 0, writer.Position);
    }
}

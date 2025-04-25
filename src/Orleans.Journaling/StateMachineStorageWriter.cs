using System.Buffers;

namespace Orleans.Journaling;

public readonly struct StateMachineStorageWriter
{
    private readonly StateMachineId _id;
    private readonly LogExtentBuilder _segment;

    internal StateMachineStorageWriter(StateMachineId id, LogExtentBuilder segment)
    {
        _id = id;
        _segment = segment;
    }

    public void AppendEntry(byte[] value) => _segment.AppendEntry(_id, value);
    public void AppendEntry(Span<byte> value) => _segment.AppendEntry(_id, value);
    public void AppendEntry(Memory<byte> value) => _segment.AppendEntry(_id, value);
    public void AppendEntry(ReadOnlyMemory<byte> value) => _segment.AppendEntry(_id, value);
    public void AppendEntry(ArraySegment<byte> value) => _segment.AppendEntry(_id, value);
    public void AppendEntry(ReadOnlySpan<byte> value) => _segment.AppendEntry(_id, value);
    public void AppendEntry(ReadOnlySequence<byte> value) => _segment.AppendEntry(_id, value);
    public void AppendEntry<T>(Action<T, IBufferWriter<byte>> valueWriter, T value) => _segment.AppendEntry(_id, valueWriter, value);
}

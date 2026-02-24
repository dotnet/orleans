using System.Buffers;
using Google.Protobuf;
using Orleans.Journaling.Protobuf.Messages;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableListEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="ListEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are wrapped in <see cref="TypedValue"/> for native encoding of well-known types.
/// </remarks>
public sealed class ProtobufListEntryCodec<T>(
    ProtobufValueConverter<T> converter) : ILogEntryCodec<DurableListEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableListEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            ListAddEntry<T>(var item) => new ListEntry
            {
                Add = new ListAdd { Item = converter.ToTypedValue(item) }
            },
            ListSetEntry<T>(var index, var item) => new ListEntry
            {
                Set = new ListSet
                {
                    Index = (uint)index,
                    Item = converter.ToTypedValue(item)
                }
            },
            ListInsertEntry<T>(var index, var item) => new ListEntry
            {
                Insert = new ListInsert
                {
                    Index = (uint)index,
                    Item = converter.ToTypedValue(item)
                }
            },
            ListRemoveAtEntry<T>(var index) => new ListEntry
            {
                RemoveAt = new ListRemoveAt { Index = (uint)index }
            },
            ListClearEntry<T> => new ListEntry { Clear = new ListClear() },
            ListSnapshotEntry<T>(var items) => CreateSnapshotMessage(items),
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableListEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = ListEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            ListEntry.CommandOneofCase.Add =>
                new ListAddEntry<T>(converter.FromTypedValue(proto.Add.Item)),
            ListEntry.CommandOneofCase.Set =>
                new ListSetEntry<T>((int)proto.Set.Index, converter.FromTypedValue(proto.Set.Item)),
            ListEntry.CommandOneofCase.Insert =>
                new ListInsertEntry<T>((int)proto.Insert.Index, converter.FromTypedValue(proto.Insert.Item)),
            ListEntry.CommandOneofCase.RemoveAt =>
                new ListRemoveAtEntry<T>((int)proto.RemoveAt.Index),
            ListEntry.CommandOneofCase.Clear =>
                new ListClearEntry<T>(),
            ListEntry.CommandOneofCase.Snapshot =>
                new ListSnapshotEntry<T>(proto.Snapshot.Items.Select(converter.FromTypedValue).ToList()),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private ListEntry CreateSnapshotMessage(IReadOnlyList<T> items)
    {
        var snapshot = new ListSnapshot();
        foreach (var item in items)
        {
            snapshot.Items.Add(converter.ToTypedValue(item));
        }

        return new ListEntry { Snapshot = snapshot };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableQueueEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="QueueEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are wrapped in <see cref="TypedValue"/> for native encoding of well-known types.
/// </remarks>
public sealed class ProtobufQueueEntryCodec<T>(
    ProtobufValueConverter<T> converter) : ILogEntryCodec<DurableQueueEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableQueueEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            QueueEnqueueEntry<T>(var item) => new QueueEntry
            {
                Enqueue = new QueueEnqueue { Item = converter.ToTypedValue(item) }
            },
            QueueDequeueEntry<T> => new QueueEntry { Dequeue = new QueueDequeue() },
            QueueClearEntry<T> => new QueueEntry { Clear = new QueueClear() },
            QueueSnapshotEntry<T>(var items) => CreateSnapshotMessage(items),
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableQueueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = QueueEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            QueueEntry.CommandOneofCase.Enqueue =>
                new QueueEnqueueEntry<T>(converter.FromTypedValue(proto.Enqueue.Item)),
            QueueEntry.CommandOneofCase.Dequeue =>
                new QueueDequeueEntry<T>(),
            QueueEntry.CommandOneofCase.Clear =>
                new QueueClearEntry<T>(),
            QueueEntry.CommandOneofCase.Snapshot =>
                new QueueSnapshotEntry<T>(proto.Snapshot.Items.Select(converter.FromTypedValue).ToList()),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private QueueEntry CreateSnapshotMessage(IReadOnlyList<T> items)
    {
        var snapshot = new QueueSnapshot();
        foreach (var item in items)
        {
            snapshot.Items.Add(converter.ToTypedValue(item));
        }

        return new QueueEntry { Snapshot = snapshot };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableSetEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="SetEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are wrapped in <see cref="TypedValue"/> for native encoding of well-known types.
/// </remarks>
public sealed class ProtobufSetEntryCodec<T>(
    ProtobufValueConverter<T> converter) : ILogEntryCodec<DurableSetEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableSetEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            SetAddEntry<T>(var item) => new SetEntry
            {
                Add = new SetAdd { Item = converter.ToTypedValue(item) }
            },
            SetRemoveEntry<T>(var item) => new SetEntry
            {
                Remove = new SetRemove { Item = converter.ToTypedValue(item) }
            },
            SetClearEntry<T> => new SetEntry { Clear = new SetClear() },
            SetSnapshotEntry<T>(var items) => CreateSnapshotMessage(items),
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableSetEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = SetEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            SetEntry.CommandOneofCase.Add =>
                new SetAddEntry<T>(converter.FromTypedValue(proto.Add.Item)),
            SetEntry.CommandOneofCase.Remove =>
                new SetRemoveEntry<T>(converter.FromTypedValue(proto.Remove.Item)),
            SetEntry.CommandOneofCase.Clear =>
                new SetClearEntry<T>(),
            SetEntry.CommandOneofCase.Snapshot =>
                new SetSnapshotEntry<T>(proto.Snapshot.Items.Select(converter.FromTypedValue).ToList()),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private SetEntry CreateSnapshotMessage(IReadOnlyList<T> items)
    {
        var snapshot = new SetSnapshot();
        foreach (var item in items)
        {
            snapshot.Items.Add(converter.ToTypedValue(item));
        }

        return new SetEntry { Snapshot = snapshot };
    }
}

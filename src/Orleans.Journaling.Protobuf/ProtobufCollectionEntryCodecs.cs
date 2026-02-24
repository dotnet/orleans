using System.Buffers;
using Google.Protobuf;
using Orleans.Journaling.Protobuf.Messages;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableListEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="Messages.ListEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are embedded as <c>bytes</c> fields serialized via <see cref="ILogDataCodec{T}"/>.
/// </remarks>
public sealed class ProtobufListEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableListEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableListEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            ListAddEntry<T>(var item) => new Messages.ListEntry
            {
                Add = new ListAdd { Item = ProtobufCodecHelper.SerializeValue(codec, item) }
            },
            ListSetEntry<T>(var index, var item) => new Messages.ListEntry
            {
                Set = new Messages.ListSet
                {
                    Index = (uint)index,
                    Item = ProtobufCodecHelper.SerializeValue(codec, item)
                }
            },
            ListInsertEntry<T>(var index, var item) => new Messages.ListEntry
            {
                Insert = new ListInsert
                {
                    Index = (uint)index,
                    Item = ProtobufCodecHelper.SerializeValue(codec, item)
                }
            },
            ListRemoveAtEntry<T>(var index) => new Messages.ListEntry
            {
                RemoveAt = new ListRemoveAt { Index = (uint)index }
            },
            ListClearEntry<T> => new Messages.ListEntry { Clear = new ListClear() },
            ListSnapshotEntry<T>(var items) => CreateSnapshotMessage(items),
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableListEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = Messages.ListEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            Messages.ListEntry.CommandOneofCase.Add =>
                new ListAddEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, proto.Add.Item)),
            Messages.ListEntry.CommandOneofCase.Set =>
                new ListSetEntry<T>((int)proto.Set.Index, ProtobufCodecHelper.DeserializeValue(codec, proto.Set.Item)),
            Messages.ListEntry.CommandOneofCase.Insert =>
                new ListInsertEntry<T>((int)proto.Insert.Index, ProtobufCodecHelper.DeserializeValue(codec, proto.Insert.Item)),
            Messages.ListEntry.CommandOneofCase.RemoveAt =>
                new ListRemoveAtEntry<T>((int)proto.RemoveAt.Index),
            Messages.ListEntry.CommandOneofCase.Clear =>
                new ListClearEntry<T>(),
            Messages.ListEntry.CommandOneofCase.Snapshot =>
                new ListSnapshotEntry<T>(proto.Snapshot.Items.Select(b => ProtobufCodecHelper.DeserializeValue(codec, b)).ToList()),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private Messages.ListEntry CreateSnapshotMessage(IReadOnlyList<T> items)
    {
        var snapshot = new ListSnapshot();
        foreach (var item in items)
        {
            snapshot.Items.Add(ProtobufCodecHelper.SerializeValue(codec, item));
        }

        return new Messages.ListEntry { Snapshot = snapshot };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableQueueEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="Messages.QueueEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are embedded as <c>bytes</c> fields serialized via <see cref="ILogDataCodec{T}"/>.
/// </remarks>
public sealed class ProtobufQueueEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableQueueEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableQueueEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            QueueEnqueueEntry<T>(var item) => new Messages.QueueEntry
            {
                Enqueue = new QueueEnqueue { Item = ProtobufCodecHelper.SerializeValue(codec, item) }
            },
            QueueDequeueEntry<T> => new Messages.QueueEntry { Dequeue = new QueueDequeue() },
            QueueClearEntry<T> => new Messages.QueueEntry { Clear = new QueueClear() },
            QueueSnapshotEntry<T>(var items) => CreateSnapshotMessage(items),
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableQueueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = Messages.QueueEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            Messages.QueueEntry.CommandOneofCase.Enqueue =>
                new QueueEnqueueEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, proto.Enqueue.Item)),
            Messages.QueueEntry.CommandOneofCase.Dequeue =>
                new QueueDequeueEntry<T>(),
            Messages.QueueEntry.CommandOneofCase.Clear =>
                new QueueClearEntry<T>(),
            Messages.QueueEntry.CommandOneofCase.Snapshot =>
                new QueueSnapshotEntry<T>(proto.Snapshot.Items.Select(b => ProtobufCodecHelper.DeserializeValue(codec, b)).ToList()),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private Messages.QueueEntry CreateSnapshotMessage(IReadOnlyList<T> items)
    {
        var snapshot = new QueueSnapshot();
        foreach (var item in items)
        {
            snapshot.Items.Add(ProtobufCodecHelper.SerializeValue(codec, item));
        }

        return new Messages.QueueEntry { Snapshot = snapshot };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableSetEntry{T}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="Messages.SetEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User values are embedded as <c>bytes</c> fields serialized via <see cref="ILogDataCodec{T}"/>.
/// </remarks>
public sealed class ProtobufSetEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableSetEntry<T>>
{
    /// <inheritdoc/>
    public void Write(DurableSetEntry<T> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            SetAddEntry<T>(var item) => new Messages.SetEntry
            {
                Add = new SetAdd { Item = ProtobufCodecHelper.SerializeValue(codec, item) }
            },
            SetRemoveEntry<T>(var item) => new Messages.SetEntry
            {
                Remove = new SetRemove { Item = ProtobufCodecHelper.SerializeValue(codec, item) }
            },
            SetClearEntry<T> => new Messages.SetEntry { Clear = new SetClear() },
            SetSnapshotEntry<T>(var items) => CreateSnapshotMessage(items),
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableSetEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var proto = Messages.SetEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            Messages.SetEntry.CommandOneofCase.Add =>
                new SetAddEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, proto.Add.Item)),
            Messages.SetEntry.CommandOneofCase.Remove =>
                new SetRemoveEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, proto.Remove.Item)),
            Messages.SetEntry.CommandOneofCase.Clear =>
                new SetClearEntry<T>(),
            Messages.SetEntry.CommandOneofCase.Snapshot =>
                new SetSnapshotEntry<T>(proto.Snapshot.Items.Select(b => ProtobufCodecHelper.DeserializeValue(codec, b)).ToList()),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private Messages.SetEntry CreateSnapshotMessage(IReadOnlyList<T> items)
    {
        var snapshot = new SetSnapshot();
        foreach (var item in items)
        {
            snapshot.Items.Add(ProtobufCodecHelper.SerializeValue(codec, item));
        }

        return new Messages.SetEntry { Snapshot = snapshot };
    }
}

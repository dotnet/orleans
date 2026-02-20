using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableListEntry{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is encoded as a protobuf message using the following field layout:
/// </para>
/// <list type="bullet">
/// <item><description>Field 1 (uint32): command (0 = add, 1 = set, 2 = insert, 3 = removeAt, 4 = clear, 5 = snapshot)</description></item>
/// <item><description>Field 2 (bytes): item value</description></item>
/// <item><description>Field 3 (uint32): index</description></item>
/// <item><description>Field 4 (uint32): item count (for snapshot)</description></item>
/// <item><description>Field 5 (bytes): repeated items (for snapshot)</description></item>
/// </list>
/// </remarks>
public sealed class ProtobufListEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableListEntry<T>>
{
    private const uint AddCommand = 0;
    private const uint SetCommand = 1;
    private const uint InsertCommand = 2;
    private const uint RemoveAtCommand = 3;
    private const uint ClearCommand = 4;
    private const uint SnapshotCommand = 5;

    private const uint TagCommand = (1 << 3) | 0;   // Field 1, varint
    private const uint TagItem = (2 << 3) | 2;      // Field 2, length-delimited
    private const uint TagIndex = (3 << 3) | 0;     // Field 3, varint
    private const uint TagCount = (4 << 3) | 0;     // Field 4, varint
    private const uint TagItems = (5 << 3) | 2;     // Field 5, length-delimited

    /// <inheritdoc/>
    public void Write(DurableListEntry<T> entry, IBufferWriter<byte> output)
    {
        var stream = new MemoryStream();
        var cos = new CodedOutputStream(stream, leaveOpen: true);

        switch (entry)
        {
            case ListAddEntry<T>(var item):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(AddCommand);
                cos.WriteTag(TagItem);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                break;
            case ListSetEntry<T>(var index, var item):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SetCommand);
                cos.WriteTag(TagIndex);
                cos.WriteUInt32((uint)index);
                cos.WriteTag(TagItem);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                break;
            case ListInsertEntry<T>(var index, var item):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(InsertCommand);
                cos.WriteTag(TagIndex);
                cos.WriteUInt32((uint)index);
                cos.WriteTag(TagItem);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                break;
            case ListRemoveAtEntry<T>(var index):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(RemoveAtCommand);
                cos.WriteTag(TagIndex);
                cos.WriteUInt32((uint)index);
                break;
            case ListClearEntry<T>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(ClearCommand);
                break;
            case ListSnapshotEntry<T>(var items):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SnapshotCommand);
                cos.WriteTag(TagCount);
                cos.WriteUInt32((uint)items.Count);
                foreach (var item in items)
                {
                    cos.WriteTag(TagItems);
                    cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                }

                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }

        cos.Flush();
        ProtobufCodecHelper.CopyToBufferWriter(stream, output);
    }

    /// <inheritdoc/>
    public DurableListEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var cis = new CodedInputStream(input.ToArray());

        uint command = 0;
        ByteString? itemBytes = null;
        uint index = 0;
        uint count = 0;
        var itemsBytesList = new List<ByteString>();

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagCommand:
                    command = cis.ReadUInt32();
                    break;
                case TagItem:
                    itemBytes = cis.ReadBytes();
                    break;
                case TagIndex:
                    index = cis.ReadUInt32();
                    break;
                case TagCount:
                    count = cis.ReadUInt32();
                    break;
                case TagItems:
                    itemsBytesList.Add(cis.ReadBytes());
                    break;
                default:
                    ProtobufCodecHelper.SkipField(cis, WireFormat.GetTagWireType(tag));
                    break;
            }
        }

        return command switch
        {
            AddCommand => new ListAddEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, itemBytes!)),
            SetCommand => new ListSetEntry<T>((int)index, ProtobufCodecHelper.DeserializeValue(codec, itemBytes!)),
            InsertCommand => new ListInsertEntry<T>((int)index, ProtobufCodecHelper.DeserializeValue(codec, itemBytes!)),
            RemoveAtCommand => new ListRemoveAtEntry<T>((int)index),
            ClearCommand => new ListClearEntry<T>(),
            SnapshotCommand => new ListSnapshotEntry<T>(
                itemsBytesList.Select(b => ProtobufCodecHelper.DeserializeValue(codec, b)).ToList()),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableQueueEntry{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is encoded as a protobuf message using the following field layout:
/// </para>
/// <list type="bullet">
/// <item><description>Field 1 (uint32): command (0 = enqueue, 1 = dequeue, 2 = clear, 3 = snapshot)</description></item>
/// <item><description>Field 2 (bytes): item value</description></item>
/// <item><description>Field 3 (uint32): item count (for snapshot)</description></item>
/// <item><description>Field 4 (bytes): repeated items (for snapshot)</description></item>
/// </list>
/// </remarks>
public sealed class ProtobufQueueEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableQueueEntry<T>>
{
    private const uint EnqueueCommand = 0;
    private const uint DequeueCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    private const uint TagCommand = (1 << 3) | 0;   // Field 1, varint
    private const uint TagItem = (2 << 3) | 2;      // Field 2, length-delimited
    private const uint TagCount = (3 << 3) | 0;     // Field 3, varint
    private const uint TagItems = (4 << 3) | 2;     // Field 4, length-delimited

    /// <inheritdoc/>
    public void Write(DurableQueueEntry<T> entry, IBufferWriter<byte> output)
    {
        var stream = new MemoryStream();
        var cos = new CodedOutputStream(stream, leaveOpen: true);

        switch (entry)
        {
            case QueueEnqueueEntry<T>(var item):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(EnqueueCommand);
                cos.WriteTag(TagItem);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                break;
            case QueueDequeueEntry<T>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(DequeueCommand);
                break;
            case QueueClearEntry<T>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(ClearCommand);
                break;
            case QueueSnapshotEntry<T>(var items):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SnapshotCommand);
                cos.WriteTag(TagCount);
                cos.WriteUInt32((uint)items.Count);
                foreach (var item in items)
                {
                    cos.WriteTag(TagItems);
                    cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                }

                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }

        cos.Flush();
        ProtobufCodecHelper.CopyToBufferWriter(stream, output);
    }

    /// <inheritdoc/>
    public DurableQueueEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var cis = new CodedInputStream(input.ToArray());

        uint command = 0;
        ByteString? itemBytes = null;
        uint count = 0;
        var itemsBytesList = new List<ByteString>();

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagCommand:
                    command = cis.ReadUInt32();
                    break;
                case TagItem:
                    itemBytes = cis.ReadBytes();
                    break;
                case TagCount:
                    count = cis.ReadUInt32();
                    break;
                case TagItems:
                    itemsBytesList.Add(cis.ReadBytes());
                    break;
                default:
                    ProtobufCodecHelper.SkipField(cis, WireFormat.GetTagWireType(tag));
                    break;
            }
        }

        return command switch
        {
            EnqueueCommand => new QueueEnqueueEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, itemBytes!)),
            DequeueCommand => new QueueDequeueEntry<T>(),
            ClearCommand => new QueueClearEntry<T>(),
            SnapshotCommand => new QueueSnapshotEntry<T>(
                itemsBytesList.Select(b => ProtobufCodecHelper.DeserializeValue(codec, b)).ToList()),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }
}

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableSetEntry{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is encoded as a protobuf message using the following field layout:
/// </para>
/// <list type="bullet">
/// <item><description>Field 1 (uint32): command (0 = add, 1 = remove, 2 = clear, 3 = snapshot)</description></item>
/// <item><description>Field 2 (bytes): item value</description></item>
/// <item><description>Field 3 (uint32): item count (for snapshot)</description></item>
/// <item><description>Field 4 (bytes): repeated items (for snapshot)</description></item>
/// </list>
/// </remarks>
public sealed class ProtobufSetEntryCodec<T>(
    ILogDataCodec<T> codec) : ILogEntryCodec<DurableSetEntry<T>>
{
    private const uint AddCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    private const uint TagCommand = (1 << 3) | 0;   // Field 1, varint
    private const uint TagItem = (2 << 3) | 2;      // Field 2, length-delimited
    private const uint TagCount = (3 << 3) | 0;     // Field 3, varint
    private const uint TagItems = (4 << 3) | 2;     // Field 4, length-delimited

    /// <inheritdoc/>
    public void Write(DurableSetEntry<T> entry, IBufferWriter<byte> output)
    {
        var stream = new MemoryStream();
        var cos = new CodedOutputStream(stream, leaveOpen: true);

        switch (entry)
        {
            case SetAddEntry<T>(var item):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(AddCommand);
                cos.WriteTag(TagItem);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                break;
            case SetRemoveEntry<T>(var item):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(RemoveCommand);
                cos.WriteTag(TagItem);
                cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                break;
            case SetClearEntry<T>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(ClearCommand);
                break;
            case SetSnapshotEntry<T>(var items):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SnapshotCommand);
                cos.WriteTag(TagCount);
                cos.WriteUInt32((uint)items.Count);
                foreach (var item in items)
                {
                    cos.WriteTag(TagItems);
                    cos.WriteBytes(ProtobufCodecHelper.SerializeValue(codec, item));
                }

                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }

        cos.Flush();
        ProtobufCodecHelper.CopyToBufferWriter(stream, output);
    }

    /// <inheritdoc/>
    public DurableSetEntry<T> Read(ReadOnlySequence<byte> input)
    {
        var cis = new CodedInputStream(input.ToArray());

        uint command = 0;
        ByteString? itemBytes = null;
        uint count = 0;
        var itemsBytesList = new List<ByteString>();

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagCommand:
                    command = cis.ReadUInt32();
                    break;
                case TagItem:
                    itemBytes = cis.ReadBytes();
                    break;
                case TagCount:
                    count = cis.ReadUInt32();
                    break;
                case TagItems:
                    itemsBytesList.Add(cis.ReadBytes());
                    break;
                default:
                    ProtobufCodecHelper.SkipField(cis, WireFormat.GetTagWireType(tag));
                    break;
            }
        }

        return command switch
        {
            AddCommand => new SetAddEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, itemBytes!)),
            RemoveCommand => new SetRemoveEntry<T>(ProtobufCodecHelper.DeserializeValue(codec, itemBytes!)),
            ClearCommand => new SetClearEntry<T>(),
            SnapshotCommand => new SetSnapshotEntry<T>(
                itemsBytesList.Select(b => ProtobufCodecHelper.DeserializeValue(codec, b)).ToList()),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }
}

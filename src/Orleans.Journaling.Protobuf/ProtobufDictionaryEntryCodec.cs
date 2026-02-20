using System.Buffers;
using Google.Protobuf;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableDictionaryEntry{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Each entry is encoded as a protobuf message using the following field layout:
/// </para>
/// <list type="bullet">
/// <item><description>Field 1 (uint32): command discriminator (0 = set, 1 = remove, 2 = clear, 3 = snapshot)</description></item>
/// <item><description>Field 2 (bytes): key value (serialized via <see cref="ILogDataCodec{T}"/>)</description></item>
/// <item><description>Field 3 (bytes): value (serialized via <see cref="ILogDataCodec{T}"/>)</description></item>
/// <item><description>Field 4 (uint32): item count (for snapshot)</description></item>
/// <item><description>Field 5 (bytes): repeated item data (for snapshot, alternating key then value)</description></item>
/// </list>
/// <example>
/// <code>
/// var codec = new ProtobufDictionaryEntryCodec&lt;string, int&gt;(keyCodec, valueCodec);
/// codec.Write(new DictionarySetEntry&lt;string, int&gt;("key", 42), bufferWriter);
/// </code>
/// </example>
/// </remarks>
public sealed class ProtobufDictionaryEntryCodec<TKey, TValue>(
    ILogDataCodec<TKey> keyCodec,
    ILogDataCodec<TValue> valueCodec) : ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>>
{
    private const uint SetCommand = 0;
    private const uint RemoveCommand = 1;
    private const uint ClearCommand = 2;
    private const uint SnapshotCommand = 3;

    // Protobuf field tags (field number << 3 | wire type).
    private const uint TagCommand = (1 << 3) | 0;      // Field 1, varint
    private const uint TagKey = (2 << 3) | 2;           // Field 2, length-delimited
    private const uint TagValue = (3 << 3) | 2;         // Field 3, length-delimited
    private const uint TagCount = (4 << 3) | 0;         // Field 4, varint
    private const uint TagItem = (5 << 3) | 2;          // Field 5, length-delimited

    /// <inheritdoc/>
    public void Write(DurableDictionaryEntry<TKey, TValue> entry, IBufferWriter<byte> output)
    {
        var stream = new MemoryStream();
        var cos = new CodedOutputStream(stream, leaveOpen: true);

        switch (entry)
        {
            case DictionarySetEntry<TKey, TValue>(var key, var value):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SetCommand);
                cos.WriteTag(TagKey);
                cos.WriteBytes(SerializeValue(keyCodec, key));
                cos.WriteTag(TagValue);
                cos.WriteBytes(SerializeValue(valueCodec, value));
                break;
            case DictionaryRemoveEntry<TKey, TValue>(var key):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(RemoveCommand);
                cos.WriteTag(TagKey);
                cos.WriteBytes(SerializeValue(keyCodec, key));
                break;
            case DictionaryClearEntry<TKey, TValue>:
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(ClearCommand);
                break;
            case DictionarySnapshotEntry<TKey, TValue>(var items):
                cos.WriteTag(TagCommand);
                cos.WriteUInt32(SnapshotCommand);
                cos.WriteTag(TagCount);
                cos.WriteUInt32((uint)items.Count);
                foreach (var (key, value) in items)
                {
                    cos.WriteTag(TagItem);
                    cos.WriteBytes(SerializeValue(keyCodec, key));
                    cos.WriteTag(TagItem);
                    cos.WriteBytes(SerializeValue(valueCodec, value));
                }

                break;
            default:
                throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}");
        }

        cos.Flush();
        CopyToBufferWriter(stream, output);
    }

    /// <inheritdoc/>
    public DurableDictionaryEntry<TKey, TValue> Read(ReadOnlySequence<byte> input)
    {
        var cis = new CodedInputStream(input.ToArray());

        uint command = 0;
        ByteString? keyBytes = null;
        ByteString? valueBytes = null;
        uint count = 0;
        var itemBytesList = new List<ByteString>();

        while (!cis.IsAtEnd)
        {
            var tag = cis.ReadTag();
            switch (tag)
            {
                case TagCommand:
                    command = cis.ReadUInt32();
                    break;
                case TagKey:
                    keyBytes = cis.ReadBytes();
                    break;
                case TagValue:
                    valueBytes = cis.ReadBytes();
                    break;
                case TagCount:
                    count = cis.ReadUInt32();
                    break;
                case TagItem:
                    itemBytesList.Add(cis.ReadBytes());
                    break;
                default:
                    ProtobufCodecHelper.SkipField(cis, WireFormat.GetTagWireType(tag));
                    break;
            }
        }

        return command switch
        {
            SetCommand => new DictionarySetEntry<TKey, TValue>(
                DeserializeValue(keyCodec, keyBytes!),
                DeserializeValue(valueCodec, valueBytes!)),
            RemoveCommand => new DictionaryRemoveEntry<TKey, TValue>(
                DeserializeValue(keyCodec, keyBytes!)),
            ClearCommand => new DictionaryClearEntry<TKey, TValue>(),
            SnapshotCommand => ReadSnapshot(count, itemBytesList),
            _ => throw new NotSupportedException($"Command type {command} is not supported"),
        };
    }

    private DictionarySnapshotEntry<TKey, TValue> ReadSnapshot(uint count, List<ByteString> itemBytesList)
    {
        var items = new List<KeyValuePair<TKey, TValue>>((int)count);
        for (var i = 0; i + 1 < itemBytesList.Count; i += 2)
        {
            var key = DeserializeValue(keyCodec, itemBytesList[i]);
            var value = DeserializeValue(valueCodec, itemBytesList[i + 1]);
            items.Add(new KeyValuePair<TKey, TValue>(key, value));
        }

        return new DictionarySnapshotEntry<TKey, TValue>(items);
    }

    private static ByteString SerializeValue<T>(ILogDataCodec<T> codec, T value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(value, buffer);
        return ByteString.CopyFrom(buffer.WrittenSpan);
    }

    private static T DeserializeValue<T>(ILogDataCodec<T> codec, ByteString bytes)
    {
        return codec.Read(new ReadOnlySequence<byte>(bytes.Memory), out _);
    }

    private static void CopyToBufferWriter(MemoryStream stream, IBufferWriter<byte> output)
    {
        var data = stream.GetBuffer().AsSpan(0, (int)stream.Length);
        var dest = output.GetSpan(data.Length);
        data.CopyTo(dest);
        output.Advance(data.Length);
    }
}

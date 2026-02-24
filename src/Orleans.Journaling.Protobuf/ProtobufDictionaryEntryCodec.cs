using System.Buffers;
using Google.Protobuf;
using Orleans.Journaling.Protobuf.Messages;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableDictionaryEntry{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="Messages.DictionaryEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User keys and values are embedded as <c>bytes</c> fields serialized via <see cref="ILogDataCodec{T}"/>.
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
    /// <inheritdoc/>
    public void Write(DurableDictionaryEntry<TKey, TValue> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            DictionarySetEntry<TKey, TValue>(var key, var value) => new Messages.DictionaryEntry
            {
                Set = new DictionarySet
                {
                    Key = ProtobufCodecHelper.SerializeValue(keyCodec, key),
                    Value = ProtobufCodecHelper.SerializeValue(valueCodec, value)
                }
            },
            DictionaryRemoveEntry<TKey, TValue>(var key) => new Messages.DictionaryEntry
            {
                Remove = new DictionaryRemove
                {
                    Key = ProtobufCodecHelper.SerializeValue(keyCodec, key)
                }
            },
            DictionaryClearEntry<TKey, TValue> => new Messages.DictionaryEntry
            {
                Clear = new DictionaryClear()
            },
            DictionarySnapshotEntry<TKey, TValue>(var items) => CreateSnapshotMessage(items),
            _ => throw new NotSupportedException($"Unsupported entry type: {entry.GetType()}")
        };

        proto.WriteTo(output);
    }

    /// <inheritdoc/>
    public DurableDictionaryEntry<TKey, TValue> Read(ReadOnlySequence<byte> input)
    {
        var proto = Messages.DictionaryEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            Messages.DictionaryEntry.CommandOneofCase.Set =>
                new DictionarySetEntry<TKey, TValue>(
                    ProtobufCodecHelper.DeserializeValue(keyCodec, proto.Set.Key),
                    ProtobufCodecHelper.DeserializeValue(valueCodec, proto.Set.Value)),
            Messages.DictionaryEntry.CommandOneofCase.Remove =>
                new DictionaryRemoveEntry<TKey, TValue>(
                    ProtobufCodecHelper.DeserializeValue(keyCodec, proto.Remove.Key)),
            Messages.DictionaryEntry.CommandOneofCase.Clear =>
                new DictionaryClearEntry<TKey, TValue>(),
            Messages.DictionaryEntry.CommandOneofCase.Snapshot =>
                ReadSnapshot(proto.Snapshot),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private Messages.DictionaryEntry CreateSnapshotMessage(IReadOnlyList<KeyValuePair<TKey, TValue>> items)
    {
        var snapshot = new DictionarySnapshot();
        foreach (var (key, value) in items)
        {
            snapshot.Items.Add(new DictionarySnapshotItem
            {
                Key = ProtobufCodecHelper.SerializeValue(keyCodec, key),
                Value = ProtobufCodecHelper.SerializeValue(valueCodec, value)
            });
        }

        return new Messages.DictionaryEntry { Snapshot = snapshot };
    }

    private DictionarySnapshotEntry<TKey, TValue> ReadSnapshot(DictionarySnapshot snapshot)
    {
        var items = new List<KeyValuePair<TKey, TValue>>(snapshot.Items.Count);
        foreach (var item in snapshot.Items)
        {
            items.Add(new KeyValuePair<TKey, TValue>(
                ProtobufCodecHelper.DeserializeValue(keyCodec, item.Key),
                ProtobufCodecHelper.DeserializeValue(valueCodec, item.Value)));
        }

        return new DictionarySnapshotEntry<TKey, TValue>(items);
    }
}

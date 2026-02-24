using System.Buffers;
using Google.Protobuf;
using Orleans.Journaling.Protobuf.Messages;

namespace Orleans.Journaling.Protobuf;

/// <summary>
/// Protocol Buffers <see cref="ILogEntryCodec{TEntry}"/> for <see cref="DurableDictionaryEntry{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// Serialized as a <see cref="DictionaryEntry"/> protobuf message with a <c>oneof command</c> discriminator.
/// User keys and values are wrapped in <see cref="TypedValue"/> for native encoding of well-known types.
/// <example>
/// <code>
/// var codec = new ProtobufDictionaryEntryCodec&lt;string, int&gt;(keyConverter, valueConverter);
/// codec.Write(new DictionarySetEntry&lt;string, int&gt;("key", 42), bufferWriter);
/// </code>
/// </example>
/// </remarks>
public sealed class ProtobufDictionaryEntryCodec<TKey, TValue>(
    ProtobufValueConverter<TKey> keyConverter,
    ProtobufValueConverter<TValue> valueConverter) : ILogEntryCodec<DurableDictionaryEntry<TKey, TValue>>
{
    /// <inheritdoc/>
    public void Write(DurableDictionaryEntry<TKey, TValue> entry, IBufferWriter<byte> output)
    {
        var proto = entry switch
        {
            DictionarySetEntry<TKey, TValue>(var key, var value) => new DictionaryEntry
            {
                Set = new DictionarySet
                {
                    Key = keyConverter.ToTypedValue(key),
                    Value = valueConverter.ToTypedValue(value)
                }
            },
            DictionaryRemoveEntry<TKey, TValue>(var key) => new DictionaryEntry
            {
                Remove = new DictionaryRemove
                {
                    Key = keyConverter.ToTypedValue(key)
                }
            },
            DictionaryClearEntry<TKey, TValue> => new DictionaryEntry
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
        var proto = DictionaryEntry.Parser.ParseFrom(input);

        return proto.CommandCase switch
        {
            DictionaryEntry.CommandOneofCase.Set =>
                new DictionarySetEntry<TKey, TValue>(
                    keyConverter.FromTypedValue(proto.Set.Key),
                    valueConverter.FromTypedValue(proto.Set.Value)),
            DictionaryEntry.CommandOneofCase.Remove =>
                new DictionaryRemoveEntry<TKey, TValue>(
                    keyConverter.FromTypedValue(proto.Remove.Key)),
            DictionaryEntry.CommandOneofCase.Clear =>
                new DictionaryClearEntry<TKey, TValue>(),
            DictionaryEntry.CommandOneofCase.Snapshot =>
                ReadSnapshot(proto.Snapshot),
            _ => throw new NotSupportedException($"Command type {proto.CommandCase} is not supported"),
        };
    }

    private DictionaryEntry CreateSnapshotMessage(IReadOnlyList<KeyValuePair<TKey, TValue>> items)
    {
        var snapshot = new DictionarySnapshot();
        foreach (var (key, value) in items)
        {
            snapshot.Items.Add(new DictionarySnapshotItem
            {
                Key = keyConverter.ToTypedValue(key),
                Value = valueConverter.ToTypedValue(value)
            });
        }

        return new DictionaryEntry { Snapshot = snapshot };
    }

    private DictionarySnapshotEntry<TKey, TValue> ReadSnapshot(DictionarySnapshot snapshot)
    {
        var items = new List<KeyValuePair<TKey, TValue>>(snapshot.Items.Count);
        foreach (var item in snapshot.Items)
        {
            items.Add(new KeyValuePair<TKey, TValue>(
                keyConverter.FromTypedValue(item.Key),
                valueConverter.FromTypedValue(item.Value)));
        }

        return new DictionarySnapshotEntry<TKey, TValue>(items);
    }
}

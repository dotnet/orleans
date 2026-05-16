using System.Buffers;
using System.Collections;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Codecs;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for the binary durable journal codec implementations.
/// </summary>
[TestCategory("BVT")]
public class JournalValueCodecTests
{
    private readonly SerializerSessionPool _sessionPool;
    private readonly ICodecProvider _codecProvider;

    public JournalValueCodecTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        var serviceProvider = services.BuildServiceProvider();
        _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();
    }

    [Fact]
    public void BinaryDictionaryCommandCodec_RoundTrips_Set()
    {
        var keyCodec = ValueCodec<string>();
        var valueCodec = ValueCodec<int>();
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<string, int>(keyCodec, valueCodec, _sessionPool);

        var input = CodecTestHelpers.WriteEntry(writer => codec.WriteSet("key1", 42, writer));

        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(CodecTestHelpers.ReadBuffer(input), consumer);

        Assert.Equal("key1", consumer.LastSetKey);
        Assert.Equal(42, consumer.LastSetValue);
    }

    [Fact]
    public void BinaryDictionaryCommandCodec_RoundTrips_Snapshot()
    {
        var keyCodec = ValueCodec<string>();
        var valueCodec = ValueCodec<int>();
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<string, int>(keyCodec, valueCodec, _sessionPool);

        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var input = CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(items, writer));

        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(CodecTestHelpers.ReadBuffer(input), consumer);

        Assert.Equal(2, consumer.Items.Count);
        Assert.Equal("alpha", consumer.Items[0].Key);
        Assert.Equal(2, consumer.Items[1].Value);
    }

    [Fact]
    public void BinaryListCommandCodec_Rejects_Overflowed_SnapshotCount()
    {
        var valueCodec = ValueCodec<int>();
        var codec = new OrleansBinaryDurableListCommandCodec<int>(valueCodec, _sessionPool);
        var buffer = new ArrayBufferWriter<byte>();

        WriteCommand(buffer, 5, 0x80000000);

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(buffer.WrittenMemory), new ListConsumer<int>()));

        Assert.Contains("snapshot count", exception.Message);
    }

    [Fact]
    public void BinaryListCommandCodec_Rejects_Overflowed_Index()
    {
        var valueCodec = ValueCodec<int>();
        var codec = new OrleansBinaryDurableListCommandCodec<int>(valueCodec, _sessionPool);
        var buffer = new ArrayBufferWriter<byte>();

        WriteCommand(buffer, 3, 0x80000000);

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(CodecTestHelpers.ReadBuffer(buffer.WrittenMemory), new ListConsumer<int>()));

        Assert.Contains("list index", exception.Message);
    }

    [Fact]
    public void BinaryDictionaryCommandCodec_WriteSnapshot_Rejects_MismatchedCollectionCount()
    {
        var keyCodec = ValueCodec<string>();
        var valueCodec = ValueCodec<int>();
        var codec = new OrleansBinaryDurableDictionaryCommandCodec<string, int>(keyCodec, valueCodec, _sessionPool);
        var items = new MiscountedReadOnlyCollection<KeyValuePair<string, int>>(
            1,
            new[]
            {
                new KeyValuePair<string, int>("alpha", 1),
                new KeyValuePair<string, int>("beta", 2),
            });

        var exception = Assert.Throws<InvalidOperationException>(
            () => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(items, writer)));

        Assert.Contains("did not match", exception.Message);
    }

    private IFieldCodec<T> ValueCodec<T>() => _codecProvider.GetCodec<T>();

    private sealed class DictionaryConsumer<TKey, TValue> : IDurableDictionaryCommandHandler<TKey, TValue> where TKey : notnull
    {
        public TKey? LastSetKey { get; private set; }
        public TValue? LastSetValue { get; private set; }
        public List<KeyValuePair<TKey, TValue>> Items { get; } = [];

        public void ApplySet(TKey key, TValue value)
        {
            LastSetKey = key;
            LastSetValue = value;
            Items.Add(new(key, value));
        }

        public void ApplyRemove(TKey key) { }
        public void ApplyClear() => Items.Clear();
        public void Reset(int capacityHint) => Items.Clear();
    }

    private sealed class ListConsumer<T> : IDurableListCommandHandler<T>
    {
        public void ApplyAdd(T item) { }
        public void ApplySet(int index, T item) { }
        public void ApplyInsert(int index, T item) { }
        public void ApplyRemoveAt(int index) { }
        public void ApplyClear() { }
        public void Reset(int capacityHint) { }
    }

    private sealed class MiscountedReadOnlyCollection<T>(int count, IReadOnlyCollection<T> items) : IReadOnlyCollection<T>
    {
        public int Count => count;
        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void WriteCommand(IBufferWriter<byte> output, uint command, uint? operand = null)
    {
        var writer = Writer.Create(output, session: null!);
        writer.WriteVarUInt32(command);
        if (operand is { } value)
        {
            writer.WriteVarUInt32(value);
        }
        writer.Commit();
    }
}

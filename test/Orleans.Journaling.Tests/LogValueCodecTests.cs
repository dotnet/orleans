using System.Buffers;
using System.Collections;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for the ILogValueCodec and binary durable log codec implementations.
/// </summary>
[TestCategory("BVT")]
public class LogValueCodecTests
{
    private readonly SerializerSessionPool _sessionPool;
    private readonly ICodecProvider _codecProvider;

    public LogValueCodecTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddSingleton(typeof(ILogValueCodec<>), typeof(OrleansLogValueCodec<>));
        var serviceProvider = services.BuildServiceProvider();
        _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();
    }

    [Fact]
    public void OrleansLogValueCodec_RoundTrips_Int()
    {
        var codec = new OrleansLogValueCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(42, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal(42, result);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void OrleansLogValueCodec_RoundTrips_String()
    {
        var codec = new OrleansLogValueCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write("hello world", buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal("hello world", result);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void OrleansLogValueCodec_RoundTrips_DateTime()
    {
        var codec = new OrleansLogValueCodec<DateTime>(_codecProvider.GetCodec<DateTime>(), _sessionPool);
        var expected = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(expected, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal(expected, result);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void BinaryDictionaryOperationCodec_RoundTrips_Set()
    {
        var keyCodec = new OrleansLogValueCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var valueCodec = new OrleansLogValueCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec, valueCodec);

        var buffer = new ArrayBufferWriter<byte>();
        codec.WriteSet("key1", 42, buffer);

        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal("key1", consumer.LastSetKey);
        Assert.Equal(42, consumer.LastSetValue);
    }

    [Fact]
    public void BinaryDictionaryOperationCodec_RoundTrips_Snapshot()
    {
        var keyCodec = new OrleansLogValueCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var valueCodec = new OrleansLogValueCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec, valueCodec);

        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var buffer = new ArrayBufferWriter<byte>();
        codec.WriteSnapshot(items, buffer);

        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(2, consumer.Items.Count);
        Assert.Equal("alpha", consumer.Items[0].Key);
        Assert.Equal(2, consumer.Items[1].Value);
    }

    [Fact]
    public void BinaryListOperationCodec_Rejects_Overflowed_SnapshotCount()
    {
        var valueCodec = new OrleansLogValueCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryListOperationCodec<int>(valueCodec);
        var buffer = new ArrayBufferWriter<byte>();

        WriteVersionAndCommand(buffer, 5);
        VarIntHelper.WriteVarUInt32(buffer, 0x80000000);

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), new ListConsumer<int>()));

        Assert.Contains("snapshot count", exception.Message);
    }

    [Fact]
    public void BinaryListOperationCodec_Rejects_Overflowed_Index()
    {
        var valueCodec = new OrleansLogValueCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryListOperationCodec<int>(valueCodec);
        var buffer = new ArrayBufferWriter<byte>();

        WriteVersionAndCommand(buffer, 3);
        VarIntHelper.WriteVarUInt32(buffer, 0x80000000);

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), new ListConsumer<int>()));

        Assert.Contains("list index", exception.Message);
    }

    [Fact]
    public void BinaryDictionaryOperationCodec_WriteSnapshot_Rejects_MismatchedCollectionCount()
    {
        var keyCodec = new OrleansLogValueCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var valueCodec = new OrleansLogValueCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryDictionaryOperationCodec<string, int>(keyCodec, valueCodec);
        var items = new MiscountedReadOnlyCollection<KeyValuePair<string, int>>(
            1,
            new[]
            {
                new KeyValuePair<string, int>("alpha", 1),
                new KeyValuePair<string, int>("beta", 2),
            });

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.WriteSnapshot(items, new ArrayBufferWriter<byte>()));

        Assert.Contains("did not match", exception.Message);
    }

    private sealed class DictionaryConsumer<TKey, TValue> : IDurableDictionaryOperationHandler<TKey, TValue> where TKey : notnull
    {
        public TKey? LastSetKey { get; private set; }
        public TValue? LastSetValue { get; private set; }
        public List<KeyValuePair<TKey, TValue>> Items { get; } = [];

        public void ApplySet(TKey key, TValue value)
        {
            LastSetKey = key;
            LastSetValue = value;
        }

        public void ApplyRemove(TKey key) { }
        public void ApplyClear() => Items.Clear();
        public void ApplySnapshotStart(int count) => Items.Clear();
        public void ApplySnapshotItem(TKey key, TValue value) => Items.Add(new(key, value));
    }

    private sealed class ListConsumer<T> : IDurableListOperationHandler<T>
    {
        public void ApplyAdd(T item) { }
        public void ApplySet(int index, T item) { }
        public void ApplyInsert(int index, T item) { }
        public void ApplyRemoveAt(int index) { }
        public void ApplyClear() { }
        public void ApplySnapshotStart(int count) { }
        public void ApplySnapshotItem(T item) { }
    }

    private sealed class MiscountedReadOnlyCollection<T>(int count, IReadOnlyCollection<T> items) : IReadOnlyCollection<T>
    {
        public int Count => count;
        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static void WriteVersionAndCommand(IBufferWriter<byte> output, uint command)
    {
        var span = output.GetSpan(1);
        span[0] = 0;
        output.Advance(1);
        VarIntHelper.WriteVarUInt32(output, command);
    }
}

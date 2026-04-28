using System.Buffers;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for the ILogDataCodec and binary durable log codec implementations.
/// </summary>
[TestCategory("BVT")]
public class LogDataCodecTests
{
    private readonly ServiceProvider _serviceProvider;
    private readonly SerializerSessionPool _sessionPool;
    private readonly ICodecProvider _codecProvider;

    public LogDataCodecTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddSingleton(typeof(ILogDataCodec<>), typeof(OrleansLogDataCodec<>));
        _serviceProvider = services.BuildServiceProvider();
        _sessionPool = _serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codecProvider = _serviceProvider.GetRequiredService<ICodecProvider>();
    }

    [Fact]
    public void OrleansLogDataCodec_RoundTrips_Int()
    {
        var codec = new OrleansLogDataCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(42, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal(42, result);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void OrleansLogDataCodec_RoundTrips_String()
    {
        var codec = new OrleansLogDataCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write("hello world", buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal("hello world", result);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void OrleansLogDataCodec_RoundTrips_DateTime()
    {
        var codec = new OrleansLogDataCodec<DateTime>(_codecProvider.GetCodec<DateTime>(), _sessionPool);
        var expected = new DateTime(2025, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(expected, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal(expected, result);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void BinaryDictionaryCodec_RoundTrips_Set()
    {
        var keyCodec = new OrleansLogDataCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var valueCodec = new OrleansLogDataCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryDictionaryEntryCodec<string, int>(keyCodec, valueCodec);

        var buffer = new ArrayBufferWriter<byte>();
        codec.WriteSet("key1", 42, buffer);

        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal("key1", consumer.LastSetKey);
        Assert.Equal(42, consumer.LastSetValue);
    }

    [Fact]
    public void BinaryDictionaryCodec_RoundTrips_Snapshot()
    {
        var keyCodec = new OrleansLogDataCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var valueCodec = new OrleansLogDataCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryDictionaryEntryCodec<string, int>(keyCodec, valueCodec);

        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var buffer = new ArrayBufferWriter<byte>();
        codec.WriteSnapshot(items, items.Count, buffer);

        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(2, consumer.Items.Count);
        Assert.Equal("alpha", consumer.Items[0].Key);
        Assert.Equal(2, consumer.Items[1].Value);
    }

    [Fact]
    public void BinaryWriteEntryCodec_QueueSnapshot_PreservesExistingShape()
    {
        var existing = new OrleansBinaryQueueEntryCodec<int>(CreateDataCodec<int>());
        var prototype = new OrleansBinaryExperimentalLogEntryCodec(_serviceProvider);
        var expected = new ArrayBufferWriter<byte>();
        var actual = new ArrayBufferWriter<byte>();

        existing.WriteSnapshot([20, 30], 2, expected);
        prototype.WriteEntry(new QueueLogEntries.Snapshot<int>([20, 30], 2), actual);
        var command = prototype.ReadCommand(new ReadOnlySequence<byte>(actual.WrittenMemory));
        var consumer = new QueueConsumer<int>();
        prototype.ApplyEntry<QueueLogEntries.Snapshot<int>, IDurableQueueLogEntryConsumer<int>>(
            new ReadOnlySequence<byte>(actual.WrittenMemory),
            consumer);

        Assert.Equal(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
        Assert.True(command.Is<QueueLogEntries.Snapshot<int>>());
        Assert.Equal(["snapshot:2", "snapshot-item:20", "snapshot-item:30"], consumer.Commands);
    }

    [Fact]
    public void BinaryWriteEntryCodec_DictionarySnapshot_PreservesExistingShape()
    {
        var existing = new OrleansBinaryDictionaryEntryCodec<string, int>(CreateDataCodec<string>(), CreateDataCodec<int>());
        var prototype = new OrleansBinaryExperimentalLogEntryCodec(_serviceProvider);
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var expected = new ArrayBufferWriter<byte>();
        var actual = new ArrayBufferWriter<byte>();

        existing.WriteSnapshot(items, items.Count, expected);
        prototype.WriteEntry(new DictionaryLogEntries.Snapshot<string, int>(items, items.Count), actual);
        var command = prototype.ReadCommand(new ReadOnlySequence<byte>(actual.WrittenMemory));
        var consumer = new DictionaryConsumer<string, int>();
        prototype.ApplyEntry<DictionaryLogEntries.Snapshot<string, int>, IDurableDictionaryLogEntryConsumer<string, int>>(
            new ReadOnlySequence<byte>(actual.WrittenMemory),
            consumer);

        Assert.Equal(expected.WrittenSpan.ToArray(), actual.WrittenSpan.ToArray());
        Assert.True(command.Is<DictionaryLogEntries.Snapshot<string, int>>());
        Assert.Equal(items, consumer.Items);
    }

    private ILogDataCodec<T> CreateDataCodec<T>() => _serviceProvider.GetRequiredService<ILogDataCodec<T>>();

    private sealed class DictionaryConsumer<TKey, TValue> : IDurableDictionaryLogEntryConsumer<TKey, TValue> where TKey : notnull
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

    private sealed class QueueConsumer<T> : IDurableQueueLogEntryConsumer<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");
        public void ApplyDequeue() => Commands.Add("dequeue");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }
}

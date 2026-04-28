using System.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Protobuf;
using Orleans.Serialization.Buffers;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Journaling.Protobuf.Tests;

/// <summary>
/// Round-trip tests for the direct Protocol Buffers log entry codecs.
/// </summary>
[TestCategory("BVT")]
public class ProtobufCodecTests
{
    private readonly SerializerSessionPool _sessionPool;
    private readonly ICodecProvider _codecProvider;

    public ProtobufCodecTests()
    {
        var services = new ServiceCollection();
        services.AddSerializer();
        services.AddSingleton(typeof(ILogDataCodec<>), typeof(OrleansLogDataCodec<>));
        var serviceProvider = services.BuildServiceProvider();
        _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();
    }

    [Fact]
    public void ProtobufDictionaryCodec_Set_RoundTrips()
    {
        var codec = new ProtobufDictionaryEntryCodec<string, int>(CreateConverter<string>(), CreateConverter<int>());
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet("key", 42, buffer);
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal("key", consumer.LastSetKey);
        Assert.Equal(42, consumer.LastSetValue);
    }

    [Fact]
    public void ProtobufDictionaryCodec_Snapshot_RoundTrips()
    {
        var codec = new ProtobufDictionaryEntryCodec<string, int>(CreateConverter<string>(), CreateConverter<int>());
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSnapshot(items, items.Count, buffer);
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(items, consumer.Items);
    }

    [Fact]
    public void ProtobufListCodec_Operations_RoundTrip()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateConverter<string>());
        var consumer = new ListConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("one", writer), consumer);
        Apply(codec, writer => codec.WriteSet(0, "updated", writer), consumer);
        Apply(codec, writer => codec.WriteInsert(1, "two", writer), consumer);
        Apply(codec, writer => codec.WriteRemoveAt(0, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(["three", "four"], 2, writer), consumer);

        Assert.Equal(["add:one", "set:0:updated", "insert:1:two", "remove:0", "clear", "snapshot:2", "snapshot-item:three", "snapshot-item:four"], consumer.Commands);
    }

    [Fact]
    public void ProtobufQueueCodec_Operations_RoundTrip()
    {
        var codec = new ProtobufQueueEntryCodec<int>(CreateConverter<int>());
        var consumer = new QueueConsumer<int>();

        Apply(codec, writer => codec.WriteEnqueue(10, writer), consumer);
        Apply(codec, writer => codec.WriteDequeue(writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([20, 30], 2, writer), consumer);

        Assert.Equal(["enqueue:10", "dequeue", "clear", "snapshot:2", "snapshot-item:20", "snapshot-item:30"], consumer.Commands);
    }

    [Fact]
    public void ProtobufSetCodec_Operations_RoundTrip()
    {
        var codec = new ProtobufSetEntryCodec<string>(CreateConverter<string>());
        var consumer = new SetConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("a", writer), consumer);
        Apply(codec, writer => codec.WriteRemove("a", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(["b", "c"], 2, writer), consumer);

        Assert.Equal(["add:a", "remove:a", "clear", "snapshot:2", "snapshot-item:b", "snapshot-item:c"], consumer.Commands);
    }

    [Fact]
    public void ProtobufValueCodec_Set_RoundTrips()
    {
        var codec = new ProtobufValueEntryCodec<int>(CreateConverter<int>());
        var consumer = new ValueConsumer<int>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(42, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(42, consumer.Value);
    }

    [Fact]
    public void ProtobufValueCodec_NullString_RoundTrips()
    {
        var codec = new ProtobufValueEntryCodec<string>(CreateConverter<string>());
        var consumer = new ValueConsumer<string>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(null!, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Null(consumer.Value);
    }

    [Fact]
    public void ProtobufStateCodec_SetAndClear_RoundTrip()
    {
        var codec = new ProtobufStateEntryCodec<string>(CreateConverter<string>());
        var consumer = new StateConsumer<string>();

        Apply(codec, writer => codec.WriteSet("state", 7, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);

        Assert.Equal(["set:state:7", "clear"], consumer.Commands);
    }

    [Fact]
    public void ProtobufTcsCodec_States_RoundTrip()
    {
        var codec = new ProtobufTcsEntryCodec<int>(CreateConverter<int>());
        var consumer = new TcsConsumer<int>();

        Apply(codec, writer => codec.WritePending(writer), consumer);
        Apply(codec, writer => codec.WriteCompleted(5, writer), consumer);
        Apply(codec, writer => codec.WriteFaulted(new InvalidOperationException("boom"), writer), consumer);
        Apply(codec, writer => codec.WriteCanceled(writer), consumer);

        Assert.Equal(["pending", "completed:5", "faulted:boom", "canceled"], consumer.Commands);
    }

    [Fact]
    public void ProtobufValueCodec_MissingValueField_Throws()
    {
        var codec = new ProtobufValueEntryCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 0]), new ValueConsumer<int>()));

        Assert.Contains("missing required field 'value'", exception.Message);
    }

    [Fact]
    public void ProtobufValueCodec_InvalidPayloadMarker_Throws()
    {
        var codec = new ProtobufValueEntryCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 0, 18, 1, 2]), new ValueConsumer<int>()));

        Assert.Contains("Invalid protobuf value payload marker", exception.Message);
    }

    [Fact]
    public void ProtobufValueCodec_TruncatedLengthDelimitedField_Throws()
    {
        var codec = new ProtobufValueEntryCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 0, 18, 2, 1]), new ValueConsumer<int>()));

        Assert.Contains("insufficient data", exception.Message);
    }

    [Fact]
    public void ProtobufListCodec_SnapshotCountMismatch_Throws()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateConverter<string>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 5, 32, 2, 26, 2, 1, 97]), new ListConsumer<string>()));

        Assert.Contains("declared 2 snapshot item(s) but contained 1", exception.Message);
    }

    [Fact]
    public void ProtobufLogExtentCodec_Encode_WritesDelimitedLogExtent()
    {
        var codec = new ProtobufLogExtentCodec();
        using var builder = new LogExtentBuilder();
        var writer = builder.CreateLogWriter(new(8));
        writer.AppendEntry((ReadOnlySpan<byte>)[8, 0]);

        var encoded = codec.Encode(builder);

        Assert.Equal([8, 10, 6, 8, 8, 18, 2, 8, 0], encoded);
    }

    [Fact]
    public void ProtobufLogExtentCodec_Decode_RoundTripsEntries()
    {
        var codec = new ProtobufLogExtentCodec();
        using var extent = Decode(codec, [8, 10, 6, 8, 8, 18, 2, 8, 0]);
        var entry = Assert.Single(extent.Entries);

        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal([8, 0], entry.Payload.ToArray());
    }

    [Theory]
    [InlineData(new byte[] { 4, 10, 2, 18, 0 }, "stream_id")]
    [InlineData(new byte[] { 4, 10, 2, 8, 8 }, "entry")]
    [InlineData(new byte[] { 8, 10, 6, 8 }, "insufficient data")]
    public void ProtobufLogExtentCodec_Decode_InvalidExtent_Throws(byte[] bytes, string expectedMessage)
    {
        var codec = new ProtobufLogExtentCodec();

        var exception = Assert.Throws<InvalidOperationException>(() => Decode(codec, bytes));

        Assert.Contains(expectedMessage, exception.Message);
    }

    private ProtobufValueConverter<T> CreateConverter<T>()
        => ProtobufValueConverter<T>.IsNativeType
            ? new ProtobufValueConverter<T>()
            : new ProtobufValueConverter<T>(new OrleansLogDataCodec<T>(_codecProvider.GetCodec<T>(), _sessionPool));

    private static ReadOnlySequence<byte> Sequence(byte[] bytes) => new(bytes);

    private static LogExtent Decode(IStateMachineLogExtentCodec codec, byte[] bytes)
    {
        using var buffer = new ArcBufferWriter();
        buffer.Write(bytes);
        return codec.Decode(buffer.ConsumeSlice(buffer.Length));
    }

    private static void Apply<T>(IDurableListCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableListLogEntryConsumer<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableQueueCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableQueueLogEntryConsumer<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableSetCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableSetLogEntryConsumer<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableStateCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableStateLogEntryConsumer<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableTaskCompletionSourceCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableTaskCompletionSourceLogEntryConsumer<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

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

    private sealed class ListConsumer<T> : IDurableListLogEntryConsumer<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");
        public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");
        public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
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

    private sealed class SetConsumer<T> : IDurableSetLogEntryConsumer<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplyRemove(T item) => Commands.Add($"remove:{item}");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }

    private sealed class ValueConsumer<T> : IDurableValueLogEntryConsumer<T>
    {
        public T? Value { get; private set; }
        public void ApplySet(T value) => Value = value;
    }

    private sealed class StateConsumer<T> : IDurableStateLogEntryConsumer<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplySet(T state, ulong version) => Commands.Add($"set:{state}:{version}");
        public void ApplyClear() => Commands.Add("clear");
    }

    private sealed class TcsConsumer<T> : IDurableTaskCompletionSourceLogEntryConsumer<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyPending() => Commands.Add("pending");
        public void ApplyCompleted(T value) => Commands.Add($"completed:{value}");
        public void ApplyFaulted(Exception exception) => Commands.Add($"faulted:{exception.Message}");
        public void ApplyCanceled() => Commands.Add("canceled");
    }
}

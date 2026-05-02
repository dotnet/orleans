using System.Buffers;
using System.Collections;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Journaling.Protobuf;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
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
        services.AddSingleton(typeof(ILogValueCodec<>), typeof(OrleansLogValueCodec<>));
        var serviceProvider = services.BuildServiceProvider();
        _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();
    }

    [Fact]
    public void ProtobufDictionaryCodec_Set_RoundTrips()
    {
        var codec = new ProtobufDictionaryOperationCodec<string, int>(CreateConverter<string>(), CreateConverter<int>());
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
        var codec = new ProtobufDictionaryOperationCodec<string, int>(CreateConverter<string>(), CreateConverter<int>());
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSnapshot(items, buffer);
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(items, consumer.Items);
    }

    [Fact]
    public void ProtobufListCodec_Operations_RoundTrip()
    {
        var codec = new ProtobufListOperationCodec<string>(CreateConverter<string>());
        var consumer = new ListConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("one", writer), consumer);
        Apply(codec, writer => codec.WriteSet(0, "updated", writer), consumer);
        Apply(codec, writer => codec.WriteInsert(1, "two", writer), consumer);
        Apply(codec, writer => codec.WriteRemoveAt(0, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(new[] { "three", "four" }, writer), consumer);

        Assert.Equal(["add:one", "set:0:updated", "insert:1:two", "remove:0", "clear", "snapshot:2", "snapshot-item:three", "snapshot-item:four"], consumer.Commands);
    }

    [Fact]
    public void ProtobufQueueCodec_Operations_RoundTrip()
    {
        var codec = new ProtobufQueueOperationCodec<int>(CreateConverter<int>());
        var consumer = new QueueConsumer<int>();

        Apply(codec, writer => codec.WriteEnqueue(10, writer), consumer);
        Apply(codec, writer => codec.WriteDequeue(writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(new[] { 20, 30 }, writer), consumer);

        Assert.Equal(["enqueue:10", "dequeue", "clear", "snapshot:2", "snapshot-item:20", "snapshot-item:30"], consumer.Commands);
    }

    [Fact]
    public void ProtobufSetCodec_Operations_RoundTrip()
    {
        var codec = new ProtobufSetOperationCodec<string>(CreateConverter<string>());
        var consumer = new SetConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("a", writer), consumer);
        Apply(codec, writer => codec.WriteRemove("a", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(new[] { "b", "c" }, writer), consumer);

        Assert.Equal(["add:a", "remove:a", "clear", "snapshot:2", "snapshot-item:b", "snapshot-item:c"], consumer.Commands);
    }

    [Fact]
    public void ProtobufListCodec_Rejects_Overflowed_SnapshotCount()
    {
        var codec = new ProtobufListOperationCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 5, 32, 128, 128, 128, 128, 8]), new ListConsumer<int>()));

        Assert.Contains("field 'count'", exception.Message);
    }

    [Fact]
    public void ProtobufListCodec_Rejects_Overflowed_Index()
    {
        var codec = new ProtobufListOperationCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 3, 16, 128, 128, 128, 128, 8]), new ListConsumer<int>()));

        Assert.Contains("field 'index'", exception.Message);
    }

    [Fact]
    public void ProtobufListCodec_WriteSnapshot_Rejects_MismatchedCollectionCount()
    {
        var codec = new ProtobufListOperationCodec<string>(CreateConverter<string>());
        var items = new MiscountedReadOnlyCollection<string>(1, new[] { "one", "two" });

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.WriteSnapshot(items, new ArrayBufferWriter<byte>()));

        Assert.Contains("did not match", exception.Message);
    }

    [Fact]
    public void ProtobufValueCodec_Set_RoundTrips()
    {
        var codec = new ProtobufValueOperationCodec<int>(CreateConverter<int>());
        var consumer = new ValueConsumer<int>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(42, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(42, consumer.Value);
    }

    [Fact]
    public void ProtobufValueCodec_NullString_RoundTrips()
    {
        var codec = new ProtobufValueOperationCodec<string>(CreateConverter<string>());
        var consumer = new ValueConsumer<string>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(null!, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Null(consumer.Value);
    }

    [Fact]
    public void ProtobufStateCodec_SetAndClear_RoundTrip()
    {
        var codec = new ProtobufStateOperationCodec<string>(CreateConverter<string>());
        var consumer = new StateConsumer<string>();

        Apply(codec, writer => codec.WriteSet("state", 7, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);

        Assert.Equal(["set:state:7", "clear"], consumer.Commands);
    }

    [Fact]
    public void ProtobufTcsCodec_States_RoundTrip()
    {
        var codec = new ProtobufTcsOperationCodec<int>(CreateConverter<int>());
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
        var codec = new ProtobufValueOperationCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 0]), new ValueConsumer<int>()));

        Assert.Contains("missing required field 'value'", exception.Message);
    }

    [Fact]
    public void ProtobufValueCodec_InvalidPayloadMarker_Throws()
    {
        var codec = new ProtobufValueOperationCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 0, 18, 1, 2]), new ValueConsumer<int>()));

        Assert.Contains("Invalid protobuf value payload marker", exception.Message);
    }

    [Fact]
    public void ProtobufValueCodec_TruncatedLengthDelimitedField_Throws()
    {
        var codec = new ProtobufValueOperationCodec<int>(CreateConverter<int>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 0, 18, 2, 1]), new ValueConsumer<int>()));

        Assert.Contains("insufficient data", exception.Message);
    }

    [Fact]
    public void ProtobufListCodec_SnapshotCountMismatch_Throws()
    {
        var codec = new ProtobufListOperationCodec<string>(CreateConverter<string>());

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(Sequence([8, 5, 32, 2, 26, 2, 1, 97]), new ListConsumer<string>()));

        Assert.Contains("declared 2 snapshot item(s) but contained 1", exception.Message);
    }

    [Fact]
    public void ProtobufLogFormat_WritesLengthDelimitedEntryMessages()
    {
        var format = new ProtobufLogFormat();
        using var writer = format.CreateWriter();

        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(8)), [0xAA, 0xBB]);
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(300)), [0xCC]);

        using var data = writer.GetCommittedBuffer();

        Assert.Equal(
            [
                0x06, 0x08, 0x08, 0x12, 0x02, 0xAA, 0xBB,
                0x06, 0x08, 0xAC, 0x02, 0x12, 0x01, 0xCC
            ],
            data.ToArray());
    }

    [Fact]
    public void ProtobufLogFormat_Read_ParsesConcatenatedEntries()
    {
        var format = new ProtobufLogFormat();
        using var writer = format.CreateWriter();
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(8)), [0xAA, 0xBB]);
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(300)), [0xCC]);
        using var data = writer.GetCommittedBuffer();
        var consumer = new CollectingConsumer();

        ReadAll(format, data, consumer);

        Assert.Collection(
            consumer.Entries,
            entry =>
            {
                Assert.Equal((ulong)8, entry.StreamId);
                Assert.Equal([0xAA, 0xBB], entry.Payload);
            },
            entry =>
            {
                Assert.Equal((ulong)300, entry.StreamId);
                Assert.Equal([0xCC], entry.Payload);
            });
    }

    [Fact]
    public void ProtobufLogFormat_Read_WaitsForPartialTrailingEntryWhenInputIsNotCompleted()
    {
        var format = new ProtobufLogFormat();
        using var firstWriter = format.CreateWriter();
        AppendEntry(firstWriter.CreateLogStreamWriter(new LogStreamId(8)), [0xAA, 0xBB]);
        using var firstData = firstWriter.GetCommittedBuffer();
        var firstBytes = firstData.ToArray();

        using var writer = format.CreateWriter();
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(8)), [0xAA, 0xBB]);
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(300)), [0xCC]);
        using var fullData = writer.GetCommittedBuffer();
        var partialBytes = fullData.ToArray()[..^1];
        using var data = CreateWriter(partialBytes);
        var reader = new ArcBufferReader(data);
        var consumer = new CollectingConsumer();

        var firstResult = format.TryRead(reader, consumer, isCompleted: false);
        var secondResult = format.TryRead(reader, consumer, isCompleted: false);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((ulong)8, entry.StreamId);
        Assert.Equal([0xAA, 0xBB], entry.Payload);
        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.Equal(partialBytes.Length - firstBytes.Length, reader.Length);
    }

    [Fact]
    public void ProtobufLogFormat_DisposeWithoutCommit_AbortsPendingEntry()
    {
        var format = new ProtobufLogFormat();
        using var writer = format.CreateWriter();
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(8)), [1]);
        using var beforeAbort = writer.GetCommittedBuffer();
        var committedBytes = beforeAbort.ToArray();

        using (var aborted = writer.CreateLogStreamWriter(new LogStreamId(9)).BeginEntry())
        {
            aborted.Writer.Write([2, 3, 4]);
        }

        using var afterAbort = writer.GetCommittedBuffer();
        Assert.Equal(committedBytes, afterAbort.ToArray());
    }

    [Fact]
    public void ProtobufLogFormat_Reset_ReusesWriter()
    {
        var format = new ProtobufLogFormat();
        using var writer = format.CreateWriter();
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(8)), [1]);

        writer.Reset();
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(9)), [2]);
        using var data = writer.GetCommittedBuffer();
        var consumer = new CollectingConsumer();

        ReadAll(format, data, consumer);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((ulong)9, entry.StreamId);
        Assert.Equal([2], entry.Payload);
    }

    [Fact]
    public void ProtobufLogFormat_GetCommittedBuffer_ThrowsWhenEntryIsActive()
    {
        var format = new ProtobufLogFormat();
        using var writer = format.CreateWriter();
        var entry = writer.CreateLogStreamWriter(new LogStreamId(8)).BeginEntry();
        entry.Writer.Write([1]);

        var exception = Assert.Throws<InvalidOperationException>(() =>
        {
            using var _ = writer.GetCommittedBuffer();
        });

        Assert.Contains("active entry", exception.Message, StringComparison.Ordinal);
        entry.Dispose();
    }

    [Fact]
    public void ProtobufLogFormat_Writer_RejectsWrongFormattedEntryType()
    {
        var format = new ProtobufLogFormat();
        using var writer = format.CreateWriter();
        var logWriter = writer.CreateLogStreamWriter(new LogStreamId(8));

        var exception = Assert.Throws<InvalidOperationException>(() => logWriter.AppendFormattedEntry(new TestFormattedLogEntry()));

        Assert.Contains("cannot append formatted entry", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x80 }, "truncated LogEntry length prefix")]
    [InlineData(new byte[] { 0x02, 0x08 }, "exceeds remaining input bytes")]
    [InlineData(new byte[] { 0x00 }, "empty LogEntry")]
    [InlineData(new byte[] { 0x02, 0x08, 0x01 }, "missing required payload")]
    [InlineData(new byte[] { 0x02, 0x12, 0x00 }, "missing required stream_id")]
    [InlineData(new byte[] { 0x03, 0x08, 0x80, 0x80 }, "invalid wire format")]
    [InlineData(new byte[] { 0x02, 0x10, 0x01 }, "missing required stream_id")]
    public void ProtobufLogFormat_Read_RejectsMalformedFrames(byte[] bytes, string expectedMessage)
    {
        var format = new ProtobufLogFormat();
        using var data = CreateWriter(bytes);
        var reader = new ArcBufferReader(data);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() => format.TryRead(reader, consumer, isCompleted: true));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

    private ProtobufValueConverter<T> CreateConverter<T>()
        => ProtobufValueConverter<T>.IsNativeType
            ? new ProtobufValueConverter<T>()
            : new ProtobufValueConverter<T>(new OrleansLogValueCodec<T>(_codecProvider.GetCodec<T>(), _sessionPool));

    private static ReadOnlySequence<byte> Sequence(byte[] bytes) => new(bytes);

    private static void AppendEntry(LogStreamWriter writer, ReadOnlySpan<byte> payload)
    {
        using var entry = writer.BeginEntry();
        entry.Writer.Write(payload);
        entry.Commit();
    }

    private static ArcBufferWriter CreateWriter(ReadOnlySpan<byte> bytes)
    {
        var writer = new ArcBufferWriter();
        writer.Write(bytes);
        return writer;
    }

    private static void ReadAll(ProtobufLogFormat format, ArcBuffer data, IStateMachineResolver consumer)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(data.AsReadOnlySequence());
        var reader = new ArcBufferReader(writer);
        while (format.TryRead(reader, consumer, isCompleted: true))
        {
        }
    }

    private sealed class TestFormattedLogEntry : IFormattedLogEntry
    {
        public ReadOnlyMemory<byte> Payload { get; } = new byte[] { 1, 2, 3 };
    }

    private static void Apply<T>(IDurableListOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableListOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableQueueOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableQueueOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableSetOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableSetOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableStateOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableStateOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
    }

    private static void Apply<T>(IDurableTaskCompletionSourceOperationCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);
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
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");
        public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");
        public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }

    private sealed class QueueConsumer<T> : IDurableQueueOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");
        public void ApplyDequeue() => Commands.Add("dequeue");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }

    private sealed class SetConsumer<T> : IDurableSetOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplyRemove(T item) => Commands.Add($"remove:{item}");
        public void ApplyClear() => Commands.Add("clear");
        public void ApplySnapshotStart(int count) => Commands.Add($"snapshot:{count}");
        public void ApplySnapshotItem(T item) => Commands.Add($"snapshot-item:{item}");
    }

    private sealed class ValueConsumer<T> : IDurableValueOperationHandler<T>
    {
        public T? Value { get; private set; }
        public void ApplySet(T value) => Value = value;
    }

    private sealed class StateConsumer<T> : IDurableStateOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplySet(T state, ulong version) => Commands.Add($"set:{state}:{version}");
        public void ApplyClear() => Commands.Add("clear");
    }

    private sealed class TcsConsumer<T> : IDurableTaskCompletionSourceOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyPending() => Commands.Add("pending");
        public void ApplyCompleted(T value) => Commands.Add($"completed:{value}");
        public void ApplyFaulted(Exception exception) => Commands.Add($"faulted:{exception.Message}");
        public void ApplyCanceled() => Commands.Add("canceled");
    }

    private sealed class CollectingConsumer : IStateMachineResolver, IDurableStateMachine
    {
        private LogStreamId _streamId;

        public List<(ulong StreamId, byte[] Payload)> Entries { get; } = [];

        object IDurableStateMachine.OperationCodec => this;

        public IDurableStateMachine ResolveStateMachine(LogStreamId streamId)
        {
            _streamId = streamId;
            return this;
        }

        public void Apply(ReadOnlySequence<byte> payload) => Entries.Add((_streamId.Value, payload.ToArray()));

        public void Reset(LogStreamWriter storage) { }
        public void AppendEntries(LogStreamWriter writer) { }
        public void AppendSnapshot(LogStreamWriter writer) { }
        public IDurableStateMachine DeepCopy() => throw new NotSupportedException();
    }

    private sealed class MiscountedReadOnlyCollection<T>(int count, IReadOnlyCollection<T> items) : IReadOnlyCollection<T>
    {
        public int Count => count;
        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

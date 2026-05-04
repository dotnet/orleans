using System.Buffers;
using System.Collections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MessagePack;
using Orleans.Hosting;
using Orleans.Journaling.MessagePack;
using Orleans.Journaling.Tests;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.MessagePack.Tests;

[TestCategory("BVT")]
public sealed class MessagePackCodecTests
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;

    [Fact]
    public void UseMessagePackCodec_RegistersFormatFamilyByKey()
    {
        var builder = new TestSiloBuilder();

        builder.UseMessagePackCodec();

        using var serviceProvider = builder.Services.BuildServiceProvider();
        Assert.IsType<MessagePackLogFormat>(serviceProvider.GetRequiredKeyedService<ILogFormat>(MessagePackJournalingExtensions.LogFormatKey));
        Assert.Same(serviceProvider.GetRequiredService<MessagePackOperationCodecProvider>(), serviceProvider.GetRequiredKeyedService<IDurableValueOperationCodecProvider>(MessagePackJournalingExtensions.LogFormatKey));
    }

    [Fact]
    public void MessagePackListCodec_Operations_RoundTrip()
    {
        var codec = new MessagePackListOperationCodec<string>(Options);
        var consumer = new ListConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("one", writer), consumer);
        Apply(codec, writer => codec.WriteSet(0, "updated", writer), consumer);
        Apply(codec, writer => codec.WriteInsert(1, "two", writer), consumer);
        Apply(codec, writer => codec.WriteRemoveAt(0, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(new[] { "three", "four" }, writer), consumer);

        Assert.Equal(["add:one", "set:0:updated", "insert:1:two", "remove:0", "clear", "reset:2", "add:three", "add:four"], consumer.Commands);
    }

    [Fact]
    public void MessagePackDictionaryCodec_Snapshot_RoundTrips()
    {
        var codec = new MessagePackDictionaryOperationCodec<string, int>(Options);
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };

        var input = CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(items, writer));
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(input, consumer);

        Assert.Equal(items, consumer.Items);
    }

    [Fact]
    public void MessagePackQueueAndSetCodecs_RoundTrip()
    {
        var queueCodec = new MessagePackQueueOperationCodec<int>(Options);
        var queueConsumer = new QueueConsumer<int>();
        var setCodec = new MessagePackSetOperationCodec<string>(Options);
        var setConsumer = new SetConsumer<string>();

        Apply(queueCodec, writer => queueCodec.WriteEnqueue(10, writer), queueConsumer);
        Apply(queueCodec, writer => queueCodec.WriteDequeue(writer), queueConsumer);
        Apply(queueCodec, writer => queueCodec.WriteSnapshot(new[] { 20, 30 }, writer), queueConsumer);
        Apply(setCodec, writer => setCodec.WriteAdd("a", writer), setConsumer);
        Apply(setCodec, writer => setCodec.WriteRemove("a", writer), setConsumer);
        Apply(setCodec, writer => setCodec.WriteSnapshot(new[] { "b", "c" }, writer), setConsumer);

        Assert.Equal(["enqueue:10", "dequeue", "reset:2", "enqueue:20", "enqueue:30"], queueConsumer.Commands);
        Assert.Equal(["add:a", "remove:a", "reset:2", "add:b", "add:c"], setConsumer.Commands);
    }

    [Fact]
    public void MessagePackValueStateAndTcsCodecs_RoundTrip()
    {
        var valueCodec = new MessagePackValueOperationCodec<int>(Options);
        var valueConsumer = new ValueConsumer<int>();
        var stateCodec = new MessagePackStateOperationCodec<string>(Options);
        var stateConsumer = new StateConsumer<string>();
        var tcsCodec = new MessagePackTcsOperationCodec<int>(Options);
        var tcsConsumer = new TcsConsumer<int>();

        Apply(valueCodec, writer => valueCodec.WriteSet(42, writer), valueConsumer);
        Apply(stateCodec, writer => stateCodec.WriteSet("state", 7, writer), stateConsumer);
        Apply(stateCodec, writer => stateCodec.WriteClear(writer), stateConsumer);
        Apply(tcsCodec, writer => tcsCodec.WritePending(writer), tcsConsumer);
        Apply(tcsCodec, writer => tcsCodec.WriteCompleted(5, writer), tcsConsumer);
        Apply(tcsCodec, writer => tcsCodec.WriteFaulted(new InvalidOperationException("boom"), writer), tcsConsumer);
        Apply(tcsCodec, writer => tcsCodec.WriteCanceled(writer), tcsConsumer);

        Assert.Equal(42, valueConsumer.Value);
        Assert.Equal(["set:state:7", "clear"], stateConsumer.Commands);
        Assert.Equal(["pending", "completed:5", "faulted:boom", "canceled"], tcsConsumer.Commands);
    }

    [Fact]
    public void MessagePackCodec_MalformedSnapshotCount_Throws()
    {
        var codec = new MessagePackListOperationCodec<string>(Options);
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(3);
        writer.Write(5);
        writer.Write(2);
        writer.Write("one");
        writer.Flush();

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), new ListConsumer<string>()));

        Assert.Contains("declared 2 snapshot item(s) but contained 1", exception.Message);
    }

    [Fact]
    public void MessagePackListCodec_WriteSnapshot_Rejects_MismatchedCollectionCount()
    {
        var codec = new MessagePackListOperationCodec<string>(Options);
        var items = new MiscountedReadOnlyCollection<string>(1, new[] { "one", "two" });

        var exception = Assert.Throws<InvalidOperationException>(
            () => CodecTestHelpers.WriteEntry(writer => codec.WriteSnapshot(items, writer)));

        Assert.Contains("did not match", exception.Message);
    }

    [Fact]
    public void MessagePackLogFormat_WritesStandaloneEntryArrays()
    {
        var format = new MessagePackLogFormat();
        using var writer = format.CreateWriter();

        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(8)), [0xAA, 0xBB]);
        AppendEntry(writer.CreateLogStreamWriter(new LogStreamId(300)), [0xCC]);

        using var data = writer.GetCommittedBuffer();

        Assert.Equal(
            [
                0x92, 0x08, 0xC4, 0x02, 0xAA, 0xBB,
                0x92, 0xCD, 0x01, 0x2C, 0xC4, 0x01, 0xCC
            ],
            data.ToArray());
    }

    [Fact]
    public void MessagePackLogFormat_Read_ParsesConcatenatedEntries()
    {
        var format = new MessagePackLogFormat();
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
    public void MessagePackLogFormat_Read_WaitsForPartialTrailingEntryWhenInputIsNotCompleted()
    {
        var format = new MessagePackLogFormat();
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
        var reader = new LogReadBuffer(new ArcBufferReader(data), isCompleted: false);
        var consumer = new CollectingConsumer();

        var firstResult = format.TryRead(reader, consumer);
        var secondResult = format.TryRead(reader, consumer);

        var entry = Assert.Single(consumer.Entries);
        Assert.Equal((ulong)8, entry.StreamId);
        Assert.Equal([0xAA, 0xBB], entry.Payload);
        Assert.True(firstResult);
        Assert.False(secondResult);
        Assert.Equal(partialBytes.Length - firstBytes.Length, reader.Length);
    }

    [Fact]
    public void MessagePackLogFormat_DisposeWithoutCommit_AbortsPendingEntry()
    {
        var format = new MessagePackLogFormat();
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
    public void MessagePackLogFormat_Reset_ReusesWriter()
    {
        var format = new MessagePackLogFormat();
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
    public void MessagePackLogFormat_GetCommittedBuffer_ThrowsWhenEntryIsActive()
    {
        var format = new MessagePackLogFormat();
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
    public void MessagePackLogFormat_Writer_RejectsWrongFormattedEntryType()
    {
        var format = new MessagePackLogFormat();
        using var writer = format.CreateWriter();
        var logWriter = writer.CreateLogStreamWriter(new LogStreamId(8));

        var exception = Assert.Throws<InvalidOperationException>(() => logWriter.AppendFormattedEntry(new TestFormattedLogEntry()));

        Assert.Contains("cannot append formatted entry", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new byte[] { 0x91, 0x01 }, "expected entry array with 2 item(s)")]
    [InlineData(new byte[] { 0x92 }, "truncated streamId")]
    [InlineData(new byte[] { 0x92, 0x01, 0xC4, 0x02, 0xAA }, "payload length 2 exceeds")]
    [InlineData(new byte[] { 0x92, 0xFF, 0xC4, 0x00 }, "streamId must be an unsigned integer")]
    [InlineData(new byte[] { 0x92, 0x01, 0xA0 }, "payload must be a binary field")]
    [InlineData(new byte[] { 0xDC, 0x00 }, "truncated array16 length")]
    public void MessagePackLogFormat_Read_RejectsMalformedFrames(byte[] bytes, string expectedMessage)
    {
        var format = new MessagePackLogFormat();
        using var data = CreateWriter(bytes);
        var reader = new LogReadBuffer(new ArcBufferReader(data), isCompleted: true);
        var consumer = new CollectingConsumer();

        var exception = Assert.Throws<InvalidOperationException>(() => format.TryRead(reader, consumer));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
        Assert.Empty(consumer.Entries);
    }

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

    private static void ReadAll(MessagePackLogFormat format, ArcBuffer data, IStateMachineResolver consumer)
    {
        using var writer = new ArcBufferWriter();
        writer.Write(data.AsReadOnlySequence());
        var reader = new LogReadBuffer(new ArcBufferReader(writer), isCompleted: true);
        while (format.TryRead(reader, consumer))
        {
        }
    }

    private sealed class TestFormattedLogEntry : IFormattedLogEntry
    {
        public ReadOnlyMemory<byte> Payload { get; } = new byte[] { 1, 2, 3 };
    }

    private static void Apply<T>(IDurableListOperationCodec<T> codec, Action<LogStreamWriter> write, IDurableListOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableQueueOperationCodec<T> codec, Action<LogStreamWriter> write, IDurableQueueOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableSetOperationCodec<T> codec, Action<LogStreamWriter> write, IDurableSetOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableValueOperationCodec<T> codec, Action<LogStreamWriter> write, IDurableValueOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableStateOperationCodec<T> codec, Action<LogStreamWriter> write, IDurableStateOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private static void Apply<T>(IDurableTaskCompletionSourceOperationCodec<T> codec, Action<LogStreamWriter> write, IDurableTaskCompletionSourceOperationHandler<T> consumer)
    {
        codec.Apply(CodecTestHelpers.WriteEntry(write), consumer);
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class DictionaryConsumer<TKey, TValue> : IDurableDictionaryOperationHandler<TKey, TValue> where TKey : notnull
    {
        public List<KeyValuePair<TKey, TValue>> Items { get; } = [];
        public void ApplySet(TKey key, TValue value) => Items.Add(new(key, value));
        public void ApplyRemove(TKey key) => throw new NotSupportedException();
        public void ApplyClear() => Items.Clear();
        public void Reset(int capacityHint) => Items.Clear();
    }

    private sealed class ListConsumer<T> : IDurableListOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplySet(int index, T item) => Commands.Add($"set:{index}:{item}");
        public void ApplyInsert(int index, T item) => Commands.Add($"insert:{index}:{item}");
        public void ApplyRemoveAt(int index) => Commands.Add($"remove:{index}");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
    }

    private sealed class QueueConsumer<T> : IDurableQueueOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyEnqueue(T item) => Commands.Add($"enqueue:{item}");
        public void ApplyDequeue() => Commands.Add("dequeue");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
    }

    private sealed class SetConsumer<T> : IDurableSetOperationHandler<T>
    {
        public List<string> Commands { get; } = [];
        public void ApplyAdd(T item) => Commands.Add($"add:{item}");
        public void ApplyRemove(T item) => Commands.Add($"remove:{item}");
        public void ApplyClear() => Commands.Add("clear");
        public void Reset(int capacityHint) => Commands.Add($"reset:{capacityHint}");
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

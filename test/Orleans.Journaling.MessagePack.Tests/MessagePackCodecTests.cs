using System.Buffers;
using System.Collections;
using MessagePack;
using Orleans.Journaling.MessagePack;
using Orleans.Serialization.Buffers;
using Xunit;

namespace Orleans.Journaling.MessagePack.Tests;

[TestCategory("BVT")]
public sealed class MessagePackCodecTests
{
    private static readonly MessagePackSerializerOptions Options = MessagePackSerializerOptions.Standard;

    [Fact]
    public void MessagePackListCodec_Operations_RoundTrip()
    {
        var codec = new MessagePackListEntryCodec<string>(Options);
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
    public void MessagePackDictionaryCodec_Snapshot_RoundTrips()
    {
        var codec = new MessagePackDictionaryEntryCodec<string, int>(Options);
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
    public void MessagePackQueueAndSetCodecs_RoundTrip()
    {
        var queueCodec = new MessagePackQueueEntryCodec<int>(Options);
        var queueConsumer = new QueueConsumer<int>();
        var setCodec = new MessagePackSetEntryCodec<string>(Options);
        var setConsumer = new SetConsumer<string>();

        Apply(queueCodec, writer => queueCodec.WriteEnqueue(10, writer), queueConsumer);
        Apply(queueCodec, writer => queueCodec.WriteDequeue(writer), queueConsumer);
        Apply(queueCodec, writer => queueCodec.WriteSnapshot(new[] { 20, 30 }, writer), queueConsumer);
        Apply(setCodec, writer => setCodec.WriteAdd("a", writer), setConsumer);
        Apply(setCodec, writer => setCodec.WriteRemove("a", writer), setConsumer);
        Apply(setCodec, writer => setCodec.WriteSnapshot(new[] { "b", "c" }, writer), setConsumer);

        Assert.Equal(["enqueue:10", "dequeue", "snapshot:2", "snapshot-item:20", "snapshot-item:30"], queueConsumer.Commands);
        Assert.Equal(["add:a", "remove:a", "snapshot:2", "snapshot-item:b", "snapshot-item:c"], setConsumer.Commands);
    }

    [Fact]
    public void MessagePackValueStateAndTcsCodecs_RoundTrip()
    {
        var valueCodec = new MessagePackValueEntryCodec<int>(Options);
        var valueConsumer = new ValueConsumer<int>();
        var stateCodec = new MessagePackStateEntryCodec<string>(Options);
        var stateConsumer = new StateConsumer<string>();
        var tcsCodec = new MessagePackTcsEntryCodec<int>(Options);
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
    public void MessagePackLogExtentCodec_UsesFixed32FramedEntries()
    {
        var codec = new MessagePackLogExtentCodec();
        using var builder = new LogExtentBuilder();
        var writer = builder.CreateLogWriter(new(8));
        writer.AppendEntry((ReadOnlySpan<byte>)[8, 0]);

        var encoded = codec.Encode(builder);

        Assert.Equal([3, 0, 0, 0, 17, 8, 0], encoded);
        using var extent = Decode(codec, encoded);
        var entry = Assert.Single(extent.Entries);
        Assert.Equal((ulong)8, entry.StreamId.Value);
        Assert.Equal([8, 0], entry.Payload.ToArray());
    }

    [Fact]
    public void MessagePackCodec_MalformedSnapshotCount_Throws()
    {
        var codec = new MessagePackListEntryCodec<string>(Options);
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
        var codec = new MessagePackListEntryCodec<string>(Options);
        var items = new MiscountedReadOnlyCollection<string>(1, new[] { "one", "two" });

        var exception = Assert.Throws<InvalidOperationException>(
            () => codec.WriteSnapshot(items, new ArrayBufferWriter<byte>()));

        Assert.Contains("did not match", exception.Message);
    }

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

    private static void Apply<T>(IDurableValueCodec<T> codec, Action<IBufferWriter<byte>> write, IDurableValueLogEntryConsumer<T> consumer)
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
        public List<KeyValuePair<TKey, TValue>> Items { get; } = [];
        public void ApplySet(TKey key, TValue value) => Items.Add(new(key, value));
        public void ApplyRemove(TKey key) => throw new NotSupportedException();
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

    private sealed class MiscountedReadOnlyCollection<T>(int count, IReadOnlyCollection<T> items) : IReadOnlyCollection<T>
    {
        public int Count => count;
        public IEnumerator<T> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

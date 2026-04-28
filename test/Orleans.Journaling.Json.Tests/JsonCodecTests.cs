using System.Buffers;
using System.Text;
using System.Text.Json;
using Orleans.Journaling.Json;
using Xunit;

namespace Orleans.Journaling.Json.Tests;

/// <summary>
/// Round-trip and JSON-format tests for the direct JSON codecs.
/// </summary>
[TestCategory("BVT")]
public class JsonCodecTests
{
    private static readonly JsonSerializerOptions Options = new();

    [Fact]
    public void JsonDictionaryCodec_Set_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>(Options);
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet("alice", 42, buffer);
        var json = GetString(buffer);
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal("""{"cmd":"set","key":"alice","value":42}""", json);
        Assert.Equal("alice", consumer.LastSetKey);
        Assert.Equal(42, consumer.LastSetValue);
    }

    [Fact]
    public void JsonDictionaryCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>(Options);
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
        };
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSnapshot(items, items.Count, buffer);
        var json = GetString(buffer);
        var consumer = new DictionaryConsumer<string, int>();
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal("""{"cmd":"snapshot","items":[{"key":"alpha","value":1},{"key":"beta","value":2}]}""", json);
        Assert.Equal(items, consumer.Items);
    }

    [Fact]
    public void JsonListCodec_Operations_RoundTrip()
    {
        var codec = new JsonListEntryCodec<string>(Options);
        var consumer = new ListConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("one", writer), consumer);
        Apply(codec, writer => codec.WriteSet(0, "updated", writer), consumer);
        Apply(codec, writer => codec.WriteInsert(1, "two", writer), consumer);
        Apply(codec, writer => codec.WriteRemoveAt(0, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);

        Assert.Equal(["add:one", "set:0:updated", "insert:1:two", "remove:0", "clear"], consumer.Commands);
    }

    [Fact]
    public void JsonListCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonListEntryCodec<string>(Options);
        var consumer = new ListConsumer<string>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSnapshot(["one", "two"], 2, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(["snapshot:2", "snapshot-item:one", "snapshot-item:two"], consumer.Commands);
    }

    [Fact]
    public void JsonQueueCodec_Operations_RoundTrip()
    {
        var codec = new JsonQueueEntryCodec<int>(Options);
        var consumer = new QueueConsumer<int>();

        Apply(codec, writer => codec.WriteEnqueue(10, writer), consumer);
        Apply(codec, writer => codec.WriteDequeue(writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot([20, 30], 2, writer), consumer);

        Assert.Equal(["enqueue:10", "dequeue", "clear", "snapshot:2", "snapshot-item:20", "snapshot-item:30"], consumer.Commands);
    }

    [Fact]
    public void JsonSetCodec_Operations_RoundTrip()
    {
        var codec = new JsonSetEntryCodec<string>(Options);
        var consumer = new SetConsumer<string>();

        Apply(codec, writer => codec.WriteAdd("a", writer), consumer);
        Apply(codec, writer => codec.WriteRemove("a", writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);
        Apply(codec, writer => codec.WriteSnapshot(["b", "c"], 2, writer), consumer);

        Assert.Equal(["add:a", "remove:a", "clear", "snapshot:2", "snapshot-item:b", "snapshot-item:c"], consumer.Commands);
    }

    [Fact]
    public void JsonValueCodec_Set_RoundTrips()
    {
        var codec = new JsonValueEntryCodec<int>(Options);
        var consumer = new ValueConsumer<int>();
        var buffer = new ArrayBufferWriter<byte>();

        codec.WriteSet(42, buffer);
        codec.Apply(new ReadOnlySequence<byte>(buffer.WrittenMemory), consumer);

        Assert.Equal(42, consumer.Value);
    }

    [Fact]
    public void JsonStateCodec_SetAndClear_RoundTrip()
    {
        var codec = new JsonStateEntryCodec<string>(Options);
        var consumer = new StateConsumer<string>();

        Apply(codec, writer => codec.WriteSet("state", 7, writer), consumer);
        Apply(codec, writer => codec.WriteClear(writer), consumer);

        Assert.Equal(["set:state:7", "clear"], consumer.Commands);
    }

    [Fact]
    public void JsonTcsCodec_States_RoundTrip()
    {
        var codec = new JsonTcsEntryCodec<int>(Options);
        var consumer = new TcsConsumer<int>();

        Apply(codec, writer => codec.WritePending(writer), consumer);
        Apply(codec, writer => codec.WriteCompleted(5, writer), consumer);
        Apply(codec, writer => codec.WriteFaulted(new InvalidOperationException("boom"), writer), consumer);
        Apply(codec, writer => codec.WriteCanceled(writer), consumer);

        Assert.Equal(["pending", "completed:5", "faulted:boom", "canceled"], consumer.Commands);
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

    private static string GetString(ArrayBufferWriter<byte> buffer) => Encoding.UTF8.GetString(buffer.WrittenSpan);

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

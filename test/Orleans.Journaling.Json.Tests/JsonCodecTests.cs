using System.Buffers;
using System.Text;
using System.Text.Json;
using Orleans.Journaling.Json;
using Xunit;

namespace Orleans.Journaling.Json.Tests;

/// <summary>
/// Comprehensive round-trip and JSON-format tests for all JSON per-type entry codecs.
/// </summary>
[TestCategory("BVT")]
public class JsonCodecTests
{
    private static readonly JsonSerializerOptions Options = new();

    // ── Dictionary ──────────────────────────────────────────

    [Fact]
    public void JsonDictionaryCodec_Set_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>(Options);
        var entry = new DictionarySetEntry<string, int>("alice", 42);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"set\"", json);
        var set = Assert.IsType<DictionarySetEntry<string, int>>(result);
        Assert.Equal("alice", set.Key);
        Assert.Equal(42, set.Value);
    }

    [Fact]
    public void JsonDictionaryCodec_Remove_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>(Options);
        var entry = new DictionaryRemoveEntry<string, int>("bob");

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"remove\"", json);
        var remove = Assert.IsType<DictionaryRemoveEntry<string, int>>(result);
        Assert.Equal("bob", remove.Key);
    }

    [Fact]
    public void JsonDictionaryCodec_Clear_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>(Options);
        var entry = new DictionaryClearEntry<string, int>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"clear\"", json);
        Assert.IsType<DictionaryClearEntry<string, int>>(result);
    }

    [Fact]
    public void JsonDictionaryCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>(Options);
        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
            new("gamma", 3),
        };
        var entry = new DictionarySnapshotEntry<string, int>(items);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"snapshot\"", json);
        var snapshot = Assert.IsType<DictionarySnapshotEntry<string, int>>(result);
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal("alpha", snapshot.Items[0].Key);
        Assert.Equal(1, snapshot.Items[0].Value);
        Assert.Equal("gamma", snapshot.Items[2].Key);
        Assert.Equal(3, snapshot.Items[2].Value);
    }

    [Fact]
    public void JsonDictionaryCodec_Snapshot_EmptyItems_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>(Options);
        var entry = new DictionarySnapshotEntry<string, int>([]);

        var (result, _) = RoundTrip(codec, entry);

        var snapshot = Assert.IsType<DictionarySnapshotEntry<string, int>>(result);
        Assert.Empty(snapshot.Items);
    }

    [Fact]
    public void JsonDictionaryCodec_ComplexValueTypes_RoundTrips()
    {
        var codec = new JsonDictionaryEntryCodec<int, TestPerson>(Options);
        var person = new TestPerson("Alice", 30);
        var entry = new DictionarySetEntry<int, TestPerson>(1, person);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"set\"", json);
        Assert.Contains("Alice", json);
        var set = Assert.IsType<DictionarySetEntry<int, TestPerson>>(result);
        Assert.Equal(1, set.Key);
        Assert.Equal("Alice", set.Value.Name);
        Assert.Equal(30, set.Value.Age);
    }

    // ── List ────────────────────────────────────────────────

    [Fact]
    public void JsonListCodec_Add_RoundTrips()
    {
        var codec = new JsonListEntryCodec<string>(Options);
        var entry = new ListAddEntry<string>("hello");

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"add\"", json);
        var add = Assert.IsType<ListAddEntry<string>>(result);
        Assert.Equal("hello", add.Item);
    }

    [Fact]
    public void JsonListCodec_Set_RoundTrips()
    {
        var codec = new JsonListEntryCodec<int>(Options);
        var entry = new ListSetEntry<int>(3, 99);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"set\"", json);
        var set = Assert.IsType<ListSetEntry<int>>(result);
        Assert.Equal(3, set.Index);
        Assert.Equal(99, set.Item);
    }

    [Fact]
    public void JsonListCodec_Insert_RoundTrips()
    {
        var codec = new JsonListEntryCodec<string>(Options);
        var entry = new ListInsertEntry<string>(1, "inserted");

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"insert\"", json);
        var insert = Assert.IsType<ListInsertEntry<string>>(result);
        Assert.Equal(1, insert.Index);
        Assert.Equal("inserted", insert.Item);
    }

    [Fact]
    public void JsonListCodec_RemoveAt_RoundTrips()
    {
        var codec = new JsonListEntryCodec<int>(Options);
        var entry = new ListRemoveAtEntry<int>(5);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"removeAt\"", json);
        var remove = Assert.IsType<ListRemoveAtEntry<int>>(result);
        Assert.Equal(5, remove.Index);
    }

    [Fact]
    public void JsonListCodec_Clear_RoundTrips()
    {
        var codec = new JsonListEntryCodec<int>(Options);
        var entry = new ListClearEntry<int>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"clear\"", json);
        Assert.IsType<ListClearEntry<int>>(result);
    }

    [Fact]
    public void JsonListCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonListEntryCodec<string>(Options);
        var entry = new ListSnapshotEntry<string>(["one", "two", "three"]);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"snapshot\"", json);
        var snapshot = Assert.IsType<ListSnapshotEntry<string>>(result);
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal("one", snapshot.Items[0]);
        Assert.Equal("three", snapshot.Items[2]);
    }

    // ── Queue ───────────────────────────────────────────────

    [Fact]
    public void JsonQueueCodec_Enqueue_RoundTrips()
    {
        var codec = new JsonQueueEntryCodec<int>(Options);
        var entry = new QueueEnqueueEntry<int>(42);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"enqueue\"", json);
        var enqueue = Assert.IsType<QueueEnqueueEntry<int>>(result);
        Assert.Equal(42, enqueue.Item);
    }

    [Fact]
    public void JsonQueueCodec_Dequeue_RoundTrips()
    {
        var codec = new JsonQueueEntryCodec<int>(Options);
        var entry = new QueueDequeueEntry<int>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"dequeue\"", json);
        Assert.IsType<QueueDequeueEntry<int>>(result);
    }

    [Fact]
    public void JsonQueueCodec_Clear_RoundTrips()
    {
        var codec = new JsonQueueEntryCodec<string>(Options);
        var entry = new QueueClearEntry<string>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"clear\"", json);
        Assert.IsType<QueueClearEntry<string>>(result);
    }

    [Fact]
    public void JsonQueueCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonQueueEntryCodec<int>(Options);
        var entry = new QueueSnapshotEntry<int>([10, 20, 30]);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"snapshot\"", json);
        var snapshot = Assert.IsType<QueueSnapshotEntry<int>>(result);
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal(10, snapshot.Items[0]);
    }

    // ── Set ─────────────────────────────────────────────────

    [Fact]
    public void JsonSetCodec_Add_RoundTrips()
    {
        var codec = new JsonSetEntryCodec<string>(Options);
        var entry = new SetAddEntry<string>("item");

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"add\"", json);
        var add = Assert.IsType<SetAddEntry<string>>(result);
        Assert.Equal("item", add.Item);
    }

    [Fact]
    public void JsonSetCodec_Remove_RoundTrips()
    {
        var codec = new JsonSetEntryCodec<string>(Options);
        var entry = new SetRemoveEntry<string>("item");

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"remove\"", json);
        var remove = Assert.IsType<SetRemoveEntry<string>>(result);
        Assert.Equal("item", remove.Item);
    }

    [Fact]
    public void JsonSetCodec_Clear_RoundTrips()
    {
        var codec = new JsonSetEntryCodec<int>(Options);
        var entry = new SetClearEntry<int>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"clear\"", json);
        Assert.IsType<SetClearEntry<int>>(result);
    }

    [Fact]
    public void JsonSetCodec_Snapshot_RoundTrips()
    {
        var codec = new JsonSetEntryCodec<string>(Options);
        var entry = new SetSnapshotEntry<string>(["a", "b", "c"]);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"snapshot\"", json);
        var snapshot = Assert.IsType<SetSnapshotEntry<string>>(result);
        Assert.Equal(3, snapshot.Items.Count);
    }

    // ── Value ───────────────────────────────────────────────

    [Fact]
    public void JsonValueCodec_Set_RoundTrips()
    {
        var codec = new JsonValueEntryCodec<int>(Options);
        var entry = new ValueSetEntry<int>(42);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"set\"", json);
        var set = Assert.IsType<ValueSetEntry<int>>(result);
        Assert.Equal(42, set.Value);
    }

    [Fact]
    public void JsonValueCodec_ComplexType_RoundTrips()
    {
        var codec = new JsonValueEntryCodec<TestPerson>(Options);
        var entry = new ValueSetEntry<TestPerson>(new TestPerson("Bob", 25));

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("Bob", json);
        var set = Assert.IsType<ValueSetEntry<TestPerson>>(result);
        Assert.Equal("Bob", set.Value.Name);
        Assert.Equal(25, set.Value.Age);
    }

    // ── State ───────────────────────────────────────────────

    [Fact]
    public void JsonStateCodec_Set_RoundTrips()
    {
        var codec = new JsonStateEntryCodec<TestPerson>(Options);
        var entry = new StateSetEntry<TestPerson>(new TestPerson("Alice", 30), 5);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"set\"", json);
        Assert.Contains("\"Version\":5", json);
        var set = Assert.IsType<StateSetEntry<TestPerson>>(result);
        Assert.Equal("Alice", set.State.Name);
        Assert.Equal(5UL, set.Version);
    }

    [Fact]
    public void JsonStateCodec_Clear_RoundTrips()
    {
        var codec = new JsonStateEntryCodec<int>(Options);
        var entry = new StateClearEntry<int>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"clear\"", json);
        Assert.IsType<StateClearEntry<int>>(result);
    }

    // ── TaskCompletionSource ────────────────────────────────

    [Fact]
    public void JsonTcsCodec_Completed_RoundTrips()
    {
        var codec = new JsonTcsEntryCodec<int>(Options);
        var entry = new TcsCompletedEntry<int>(42);

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"completed\"", json);
        var completed = Assert.IsType<TcsCompletedEntry<int>>(result);
        Assert.Equal(42, completed.Value);
    }

    [Fact]
    public void JsonTcsCodec_Faulted_RoundTrips()
    {
        var codec = new JsonTcsEntryCodec<int>(Options);
        var entry = new TcsFaultedEntry<int>(new InvalidOperationException("test error"));

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"faulted\"", json);
        Assert.Contains("test error", json);
        var faulted = Assert.IsType<TcsFaultedEntry<int>>(result);
        Assert.Contains("test error", faulted.Exception.Message);
    }

    [Fact]
    public void JsonTcsCodec_Canceled_RoundTrips()
    {
        var codec = new JsonTcsEntryCodec<string>(Options);
        var entry = new TcsCanceledEntry<string>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"canceled\"", json);
        Assert.IsType<TcsCanceledEntry<string>>(result);
    }

    [Fact]
    public void JsonTcsCodec_Pending_RoundTrips()
    {
        var codec = new JsonTcsEntryCodec<string>(Options);
        var entry = new TcsPendingEntry<string>();

        var (result, json) = RoundTrip(codec, entry);

        Assert.Contains("\"cmd\":\"pending\"", json);
        Assert.IsType<TcsPendingEntry<string>>(result);
    }

    // ── Helpers ─────────────────────────────────────────────

    private static (T Result, string Json) RoundTrip<T>(ILogEntryCodec<T> codec, T entry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        return (result, json);
    }

    private sealed record TestPerson(string Name, int Age);
}

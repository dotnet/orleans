using System.Buffers;
using Orleans.Journaling.Protobuf;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Round-trip tests for the Protocol Buffers log entry codecs.
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
        var serviceProvider = services.BuildServiceProvider();
        _sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        _codecProvider = serviceProvider.GetRequiredService<ICodecProvider>();
    }

    private OrleansLogDataCodec<T> CreateDataCodec<T>()
        => new(_codecProvider.GetCodec<T>(), _sessionPool);

    #region Dictionary

    [Fact]
    public void ProtobufDictionaryCodec_RoundTrips_Set()
    {
        var codec = new ProtobufDictionaryEntryCodec<string, int>(CreateDataCodec<string>(), CreateDataCodec<int>());

        var entry = new DictionarySetEntry<string, int>("key", 42);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var set = Assert.IsType<DictionarySetEntry<string, int>>(result);
        Assert.Equal("key", set.Key);
        Assert.Equal(42, set.Value);
    }

    [Fact]
    public void ProtobufDictionaryCodec_RoundTrips_Remove()
    {
        var codec = new ProtobufDictionaryEntryCodec<string, int>(CreateDataCodec<string>(), CreateDataCodec<int>());

        var entry = new DictionaryRemoveEntry<string, int>("removeMe");
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var remove = Assert.IsType<DictionaryRemoveEntry<string, int>>(result);
        Assert.Equal("removeMe", remove.Key);
    }

    [Fact]
    public void ProtobufDictionaryCodec_RoundTrips_Clear()
    {
        var codec = new ProtobufDictionaryEntryCodec<string, int>(CreateDataCodec<string>(), CreateDataCodec<int>());

        var entry = new DictionaryClearEntry<string, int>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<DictionaryClearEntry<string, int>>(result);
    }

    [Fact]
    public void ProtobufDictionaryCodec_RoundTrips_Snapshot()
    {
        var codec = new ProtobufDictionaryEntryCodec<string, int>(CreateDataCodec<string>(), CreateDataCodec<int>());

        var items = new List<KeyValuePair<string, int>>
        {
            new("alpha", 1),
            new("beta", 2),
            new("gamma", 3),
        };
        var entry = new DictionarySnapshotEntry<string, int>(items);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var snapshot = Assert.IsType<DictionarySnapshotEntry<string, int>>(result);
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal("alpha", snapshot.Items[0].Key);
        Assert.Equal(1, snapshot.Items[0].Value);
        Assert.Equal("beta", snapshot.Items[1].Key);
        Assert.Equal(2, snapshot.Items[1].Value);
        Assert.Equal("gamma", snapshot.Items[2].Key);
        Assert.Equal(3, snapshot.Items[2].Value);
    }

    #endregion

    #region List

    [Fact]
    public void ProtobufListCodec_RoundTrips_Add()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateDataCodec<string>());

        var entry = new ListAddEntry<string>("hello");
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var add = Assert.IsType<ListAddEntry<string>>(result);
        Assert.Equal("hello", add.Item);
    }

    [Fact]
    public void ProtobufListCodec_RoundTrips_Set()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateDataCodec<string>());

        var entry = new ListSetEntry<string>(3, "updated");
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var set = Assert.IsType<ListSetEntry<string>>(result);
        Assert.Equal(3, set.Index);
        Assert.Equal("updated", set.Item);
    }

    [Fact]
    public void ProtobufListCodec_RoundTrips_Insert()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateDataCodec<string>());

        var entry = new ListInsertEntry<string>(1, "inserted");
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var insert = Assert.IsType<ListInsertEntry<string>>(result);
        Assert.Equal(1, insert.Index);
        Assert.Equal("inserted", insert.Item);
    }

    [Fact]
    public void ProtobufListCodec_RoundTrips_RemoveAt()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateDataCodec<string>());

        var entry = new ListRemoveAtEntry<string>(5);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var removeAt = Assert.IsType<ListRemoveAtEntry<string>>(result);
        Assert.Equal(5, removeAt.Index);
    }

    [Fact]
    public void ProtobufListCodec_RoundTrips_Clear()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateDataCodec<string>());

        var entry = new ListClearEntry<string>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<ListClearEntry<string>>(result);
    }

    [Fact]
    public void ProtobufListCodec_RoundTrips_Snapshot()
    {
        var codec = new ProtobufListEntryCodec<string>(CreateDataCodec<string>());

        var entry = new ListSnapshotEntry<string>(["one", "two", "three"]);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var snapshot = Assert.IsType<ListSnapshotEntry<string>>(result);
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal("one", snapshot.Items[0]);
        Assert.Equal("two", snapshot.Items[1]);
        Assert.Equal("three", snapshot.Items[2]);
    }

    #endregion

    #region Queue

    [Fact]
    public void ProtobufQueueCodec_RoundTrips_Enqueue()
    {
        var codec = new ProtobufQueueEntryCodec<int>(CreateDataCodec<int>());

        var entry = new QueueEnqueueEntry<int>(99);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var enqueue = Assert.IsType<QueueEnqueueEntry<int>>(result);
        Assert.Equal(99, enqueue.Item);
    }

    [Fact]
    public void ProtobufQueueCodec_RoundTrips_Dequeue()
    {
        var codec = new ProtobufQueueEntryCodec<int>(CreateDataCodec<int>());

        var entry = new QueueDequeueEntry<int>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<QueueDequeueEntry<int>>(result);
    }

    [Fact]
    public void ProtobufQueueCodec_RoundTrips_Clear()
    {
        var codec = new ProtobufQueueEntryCodec<int>(CreateDataCodec<int>());

        var entry = new QueueClearEntry<int>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<QueueClearEntry<int>>(result);
    }

    [Fact]
    public void ProtobufQueueCodec_RoundTrips_Snapshot()
    {
        var codec = new ProtobufQueueEntryCodec<int>(CreateDataCodec<int>());

        var entry = new QueueSnapshotEntry<int>([10, 20, 30]);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var snapshot = Assert.IsType<QueueSnapshotEntry<int>>(result);
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal(10, snapshot.Items[0]);
        Assert.Equal(20, snapshot.Items[1]);
        Assert.Equal(30, snapshot.Items[2]);
    }

    #endregion

    #region Set

    [Fact]
    public void ProtobufSetCodec_RoundTrips_Add()
    {
        var codec = new ProtobufSetEntryCodec<string>(CreateDataCodec<string>());

        var entry = new SetAddEntry<string>("item1");
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var add = Assert.IsType<SetAddEntry<string>>(result);
        Assert.Equal("item1", add.Item);
    }

    [Fact]
    public void ProtobufSetCodec_RoundTrips_Remove()
    {
        var codec = new ProtobufSetEntryCodec<string>(CreateDataCodec<string>());

        var entry = new SetRemoveEntry<string>("item2");
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var remove = Assert.IsType<SetRemoveEntry<string>>(result);
        Assert.Equal("item2", remove.Item);
    }

    [Fact]
    public void ProtobufSetCodec_RoundTrips_Clear()
    {
        var codec = new ProtobufSetEntryCodec<string>(CreateDataCodec<string>());

        var entry = new SetClearEntry<string>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<SetClearEntry<string>>(result);
    }

    [Fact]
    public void ProtobufSetCodec_RoundTrips_Snapshot()
    {
        var codec = new ProtobufSetEntryCodec<string>(CreateDataCodec<string>());

        var entry = new SetSnapshotEntry<string>(["a", "b", "c"]);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var snapshot = Assert.IsType<SetSnapshotEntry<string>>(result);
        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal("a", snapshot.Items[0]);
        Assert.Equal("b", snapshot.Items[1]);
        Assert.Equal("c", snapshot.Items[2]);
    }

    #endregion

    #region Value

    [Fact]
    public void ProtobufValueCodec_RoundTrips_Set()
    {
        var codec = new ProtobufValueEntryCodec<int>(CreateDataCodec<int>());

        var entry = new ValueSetEntry<int>(42);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var set = Assert.IsType<ValueSetEntry<int>>(result);
        Assert.Equal(42, set.Value);
    }

    #endregion

    #region State

    [Fact]
    public void ProtobufStateCodec_RoundTrips_Set()
    {
        var codec = new ProtobufStateEntryCodec<string>(CreateDataCodec<string>());

        var entry = new StateSetEntry<string>("myState", 7);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var set = Assert.IsType<StateSetEntry<string>>(result);
        Assert.Equal("myState", set.State);
        Assert.Equal(7UL, set.Version);
    }

    [Fact]
    public void ProtobufStateCodec_RoundTrips_Clear()
    {
        var codec = new ProtobufStateEntryCodec<string>(CreateDataCodec<string>());

        var entry = new StateClearEntry<string>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<StateClearEntry<string>>(result);
    }

    #endregion

    #region TaskCompletionSource

    [Fact]
    public void ProtobufTcsCodec_RoundTrips_Completed()
    {
        var codec = new ProtobufTcsEntryCodec<int>(CreateDataCodec<int>());

        var entry = new TcsCompletedEntry<int>(100);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var completed = Assert.IsType<TcsCompletedEntry<int>>(result);
        Assert.Equal(100, completed.Value);
    }

    [Fact]
    public void ProtobufTcsCodec_RoundTrips_Faulted()
    {
        var codec = new ProtobufTcsEntryCodec<int>(CreateDataCodec<int>());

        var entry = new TcsFaultedEntry<int>(new InvalidOperationException("test error"));
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var faulted = Assert.IsType<TcsFaultedEntry<int>>(result);
        Assert.Contains("test error", faulted.Exception.Message);
    }

    [Fact]
    public void ProtobufTcsCodec_RoundTrips_Canceled()
    {
        var codec = new ProtobufTcsEntryCodec<int>(CreateDataCodec<int>());

        var entry = new TcsCanceledEntry<int>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<TcsCanceledEntry<int>>(result);
    }

    [Fact]
    public void ProtobufTcsCodec_RoundTrips_Pending()
    {
        var codec = new ProtobufTcsEntryCodec<int>(CreateDataCodec<int>());

        var entry = new TcsPendingEntry<int>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        Assert.IsType<TcsPendingEntry<int>>(result);
    }

    #endregion
}

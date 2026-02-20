using System.Buffers;
using System.Text;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for the ILogDataCodec and ILogEntryCodec implementations.
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
    public void BinaryDictionaryCodec_RoundTrips()
    {
        var keyCodec = new OrleansLogDataCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);
        var valueCodec = new OrleansLogDataCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var codec = new OrleansBinaryDictionaryEntryCodec<string, int>(keyCodec, valueCodec);

        var entry = new DictionarySetEntry<string, int>("key1", 42);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var setResult = Assert.IsType<DictionarySetEntry<string, int>>(result);
        Assert.Equal("key1", setResult.Key);
        Assert.Equal(42, setResult.Value);
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
        var entry = new DictionarySnapshotEntry<string, int>(items);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var snapshot = Assert.IsType<DictionarySnapshotEntry<string, int>>(result);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal("alpha", snapshot.Items[0].Key);
        Assert.Equal(2, snapshot.Items[1].Value);
    }

    [Fact]
    public void JsonDictionaryCodec_RoundTrips_Set()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>();
        var entry = new DictionarySetEntry<string, int>("alice", 42);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        // Verify JSON output contains "cmd":"set"
        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Contains("\"cmd\":\"set\"", json);
        Assert.Contains("\"Key\"", json);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var setResult = Assert.IsType<DictionarySetEntry<string, int>>(result);
        Assert.Equal("alice", setResult.Key);
        Assert.Equal(42, setResult.Value);
    }

    [Fact]
    public void JsonDictionaryCodec_RoundTrips_Snapshot()
    {
        var codec = new JsonDictionaryEntryCodec<string, int>();
        var items = new List<KeyValuePair<string, int>>
        {
            new("alice", 1),
            new("bob", 7),
        };
        var entry = new DictionarySnapshotEntry<string, int>(items);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Contains("\"cmd\":\"snapshot\"", json);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var snapshot = Assert.IsType<DictionarySnapshotEntry<string, int>>(result);
        Assert.Equal(2, snapshot.Items.Count);
        Assert.Equal("alice", snapshot.Items[0].Key);
        Assert.Equal(7, snapshot.Items[1].Value);
    }

    [Fact]
    public void JsonListCodec_RoundTrips()
    {
        var codec = new JsonListEntryCodec<string>();
        var entry = new ListAddEntry<string>("hello");
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Contains("\"cmd\":\"add\"", json);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var addResult = Assert.IsType<ListAddEntry<string>>(result);
        Assert.Equal("hello", addResult.Item);
    }

    [Fact]
    public void JsonValueCodec_RoundTrips()
    {
        var codec = new JsonValueEntryCodec<int>();
        var entry = new ValueSetEntry<int>(42);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Contains("\"cmd\":\"set\"", json);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var setResult = Assert.IsType<ValueSetEntry<int>>(result);
        Assert.Equal(42, setResult.Value);
    }

    [Fact]
    public void JsonStateCodec_RoundTrips()
    {
        var codec = new JsonStateEntryCodec<TestRecord>();
        var entry = new StateSetEntry<TestRecord>(new TestRecord("Alice", 30), 5);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(entry, buffer);

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        Assert.Contains("\"cmd\":\"set\"", json);
        Assert.Contains("\"Version\":5", json);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory));
        var setResult = Assert.IsType<StateSetEntry<TestRecord>>(result);
        Assert.Equal("Alice", setResult.State.Name);
        Assert.Equal(30, setResult.State.Age);
        Assert.Equal(5UL, setResult.Version);
    }

    private record TestRecord(string Name, int Age);
}

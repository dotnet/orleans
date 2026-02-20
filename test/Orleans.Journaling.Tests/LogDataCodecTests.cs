using System.Buffers;
using Orleans.Journaling.Json;
using Orleans.Serialization;
using Orleans.Serialization.Serializers;
using Orleans.Serialization.Session;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Orleans.Journaling.Tests;

/// <summary>
/// Tests for the ILogDataCodec implementations and ILogEntryCodecFactory implementations.
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
    public void JsonLogDataCodec_RoundTrips_Int()
    {
        var codec = new JsonLogDataCodec<int>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(42, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal(42, result);
        Assert.True(consumed > 0);
    }

    [Fact]
    public void JsonLogDataCodec_RoundTrips_String()
    {
        var codec = new JsonLogDataCodec<string>();
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write("hello world", buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal("hello world", result);
    }

    [Fact]
    public void JsonLogDataCodec_RoundTrips_ComplexObject()
    {
        var codec = new JsonLogDataCodec<TestRecord>();
        var expected = new TestRecord("Alice", 30);
        var buffer = new ArrayBufferWriter<byte>();
        codec.Write(expected, buffer);

        var result = codec.Read(new ReadOnlySequence<byte>(buffer.WrittenMemory), out var consumed);

        Assert.Equal(expected.Name, result.Name);
        Assert.Equal(expected.Age, result.Age);
    }

    [Fact]
    public void OrleansBinaryEntryCodec_WriterReader_RoundTrips_Command()
    {
        var factory = new OrleansBinaryEntryCodec();
        Assert.Equal(0, factory.Version);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = factory.CreateWriter())
        {
            writer.WriteCommand(42);
            writer.WriteUInt32(100);
            writer.WriteUInt64(999UL);
            writer.WriteByte(7);
            writer.WriteTo(buffer);
        }

        // Skip version byte (written by WriteTo)
        var data = new ReadOnlySequence<byte>(buffer.WrittenMemory[1..]);
        using var reader = factory.CreateReader(data);

        Assert.Equal(42u, reader.ReadCommand());
        Assert.Equal(100u, reader.ReadUInt32());
        Assert.Equal(999UL, reader.ReadUInt64());
        Assert.Equal(7, reader.ReadByte());
    }

    [Fact]
    public void OrleansBinaryEntryCodec_WriterReader_RoundTrips_Values()
    {
        var factory = new OrleansBinaryEntryCodec();
        var intCodec = new OrleansLogDataCodec<int>(_codecProvider.GetCodec<int>(), _sessionPool);
        var stringCodec = new OrleansLogDataCodec<string>(_codecProvider.GetCodec<string>(), _sessionPool);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = factory.CreateWriter())
        {
            writer.WriteCommand(1);
            writer.WriteValue(stringCodec, "key1");
            writer.WriteValue(intCodec, 42);
            writer.WriteTo(buffer);
        }

        var data = new ReadOnlySequence<byte>(buffer.WrittenMemory[1..]);
        using var reader = factory.CreateReader(data);

        Assert.Equal(1u, reader.ReadCommand());
        Assert.Equal("key1", reader.ReadValue(stringCodec));
        Assert.Equal(42, reader.ReadValue(intCodec));
    }

    [Fact]
    public void JsonEntryCodec_WriterReader_RoundTrips()
    {
        var factory = new JsonEntryCodec(new System.Text.Json.JsonSerializerOptions());
        Assert.Equal(1, factory.Version);

        var intCodec = new JsonLogDataCodec<int>();
        var stringCodec = new JsonLogDataCodec<string>();

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = factory.CreateWriter())
        {
            writer.WriteCommand(3);
            writer.WriteUInt32(10);
            writer.WriteValue(stringCodec, "hello");
            writer.WriteValue(intCodec, 99);
            writer.WriteTo(buffer);
        }

        // Skip version byte
        var data = new ReadOnlySequence<byte>(buffer.WrittenMemory[1..]);
        using var reader = factory.CreateReader(data);

        Assert.Equal(3u, reader.ReadCommand());
        Assert.Equal(10u, reader.ReadUInt32());
        Assert.Equal("hello", reader.ReadValue(stringCodec));
        Assert.Equal(99, reader.ReadValue(intCodec));
    }

    [Fact]
    public void JsonEntryCodec_WriterReader_RoundTrips_ByteAndUInt64()
    {
        var factory = new JsonEntryCodec(new System.Text.Json.JsonSerializerOptions());

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = factory.CreateWriter())
        {
            writer.WriteByte(255);
            writer.WriteUInt64(ulong.MaxValue);
            writer.WriteTo(buffer);
        }

        var data = new ReadOnlySequence<byte>(buffer.WrittenMemory[1..]);
        using var reader = factory.CreateReader(data);

        Assert.Equal(255, reader.ReadByte());
        Assert.Equal(ulong.MaxValue, reader.ReadUInt64());
    }

    private record TestRecord(string Name, int Age);
}

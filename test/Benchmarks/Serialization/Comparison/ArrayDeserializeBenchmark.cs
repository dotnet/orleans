using System.Buffers;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Benchmarks.Serialization.Models;
using Benchmarks.Utilities;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Xunit;

namespace Benchmarks.Serialization.Comparison;

[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[Config(typeof(BenchmarkConfig))]
public class ArraySerializeBenchmark
{
    private static readonly MyVector3[] _value;
    private static readonly Serializer<MyVector3[]> _orleansSerializer;
    private static readonly SerializerSession _session;
    private static readonly ArrayBufferWriter<byte> _arrayBufferWriter;
    private static readonly Utf8JsonWriter _jsonWriter;
    private static readonly MemoryStream _stream;

    static ArraySerializeBenchmark()
    {
        _value = Enumerable.Repeat(new MyVector3 { X = 10.3f, Y = 40.5f, Z = 13411.3f }, 1000).ToArray();
        var serviceProvider = new ServiceCollection()
            .AddSerializer(builder => builder.AddAssembly(typeof(ArraySerializeBenchmark).Assembly))
            .BuildServiceProvider();
        _orleansSerializer = serviceProvider.GetRequiredService<Serializer<MyVector3[]>>();
        _session = serviceProvider.GetRequiredService<SerializerSessionPool>().GetSession();

        // create buffers
        _stream = new MemoryStream();

        var serialize1 = _orleansSerializer.SerializeToArray(_value);
        var serialize2 = MessagePackSerializer.Serialize(_value);
        ProtoBuf.Serializer.Serialize(_stream, _value);
        var serialize3 = _stream.ToArray();
        _stream.Position = 0;
        var serialize4 = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_value));

        _arrayBufferWriter = new ArrayBufferWriter<byte>(new[] { serialize1, serialize2, serialize3, serialize4 }.Max(x => x.Length));
        _jsonWriter = new Utf8JsonWriter(_arrayBufferWriter);
    }

    // return byte[]

    [Benchmark(Baseline = true), BenchmarkCategory(" byte[]")]
    public byte[] MessagePackSerialize()
    {
        return MessagePackSerializer.Serialize(_value);
    }

    [Benchmark, BenchmarkCategory(" byte[]")]
    public byte[] ProtobufNetSerialize()
    {
        ProtoBuf.Serializer.Serialize(_stream, _value);
        var array = _stream.ToArray();
        _stream.Position = 0;
        return array;
    }

    [Benchmark, BenchmarkCategory(" byte[]")]
    public byte[] SystemTextJsonSerialize()
    {
        JsonSerializer.Serialize(_stream, _value);
        var array = _stream.ToArray();
        _stream.Position = 0;
        return array;
    }

    [Benchmark, BenchmarkCategory(" byte[]")]
    public byte[] OrleansSerialize()
    {
        return _orleansSerializer.SerializeToArray(_value);
    }

    // use BufferWriter

    [Fact]
    [Benchmark(Baseline = true), BenchmarkCategory("BufferWriter")]
    public void MessagePackBufferWriter()
    {
        MessagePackSerializer.Serialize(_arrayBufferWriter, _value);
        _arrayBufferWriter.Clear();
    }

    [Fact]
    [Benchmark, BenchmarkCategory("BufferWriter")]
    public void ProtobufNetBufferWriter()
    {
        ProtoBuf.Serializer.Serialize(_arrayBufferWriter, _value);
        _arrayBufferWriter.Clear();
    }

    [Fact]
    [Benchmark, BenchmarkCategory("BufferWriter")]
    public void SystemTextJsonBufferWriter()
    {
        JsonSerializer.Serialize(_jsonWriter, _value);
        _jsonWriter.Flush();
        _arrayBufferWriter.Clear();
        _jsonWriter.Reset(_arrayBufferWriter);
    }

    [Fact]
    [Benchmark, BenchmarkCategory("BufferWriter")]
    public void OrleansBufferWriter()
    {
        var writer = Writer.CreatePooled(_session);
        try
        {
            _orleansSerializer.Serialize(_value, ref writer);
        }
        finally
        {
            writer.Dispose();
            _session.PartialReset();
        }
    }

    [Fact]
    [Benchmark, BenchmarkCategory("BufferWriter")]
    public void OrleansBufferWriter2()
    {
        // wrap ArrayBufferWriter<byte>
        var writer = _arrayBufferWriter.CreateWriter(_session);
        try
        {
            _orleansSerializer.Serialize(_value, ref writer);
        }
        finally
        {
            writer.Dispose();
            _session.PartialReset();
        }

        _arrayBufferWriter.Clear(); // clear ArrayBufferWriter<byte>
    }
}

using Microsoft.Extensions.DependencyInjection;
using Orleans.Serialization.Buffers;
using Orleans.Serialization.Session;
using Xunit;

namespace Orleans.Serialization.UnitTests.Buffers;

[Trait("Category", "BVT")]
public sealed class ArcBufferWriterSerializationTests
{
    [Fact]
    public void ArcBufferWriter_RoundTripsReaderWriterAcrossPages()
    {
        using var serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        var payload = new byte[ArcBufferWriter.MinimumPageSize + 17];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        using var bufferWriter = new ArcBufferWriter();
        using (var writerSession = sessionPool.GetSession())
        {
            var writer = Writer.Create(bufferWriter, writerSession);
            writer.WriteVarUInt32((uint)payload.Length);
            writer.Write(payload);
            writer.WriteUInt64(0xDEADBEEFUL);
            writer.Commit();
        }

        using var buffer = bufferWriter.PeekSlice(bufferWriter.Length);
        using var readerSession = sessionPool.GetSession();
        var reader = Reader.Create(buffer, readerSession);
        Assert.Equal((uint)payload.Length, reader.ReadVarUInt32());
        Assert.Equal(payload, reader.ReadBytes((uint)payload.Length));
        Assert.Equal(0xDEADBEEFUL, reader.ReadUInt64());
        Assert.Equal(reader.Length, reader.Position);
    }

    [Fact]
    public void ArcBufferWriter_DeserializesThroughSerializer()
    {
        using var serviceProvider = new ServiceCollection()
            .AddSerializer()
            .BuildServiceProvider();
        var sessionPool = serviceProvider.GetRequiredService<SerializerSessionPool>();
        var serializer = serviceProvider.GetRequiredService<Serializer>();
        var typedSerializer = serviceProvider.GetRequiredService<Serializer<string>>();
        var expected = new string('x', ArcBufferWriter.MinimumPageSize + 17);

        using var bufferWriter = new ArcBufferWriter();
        using (var writerSession = sessionPool.GetSession())
        {
            var writer = Writer.Create(bufferWriter, writerSession);
            serializer.Serialize(expected, ref writer);
            writer.Commit();
        }

        using var buffer = bufferWriter.PeekSlice(bufferWriter.Length);
        Assert.Equal(expected, serializer.Deserialize<string>(buffer));
        Assert.Equal(expected, typedSerializer.Deserialize(buffer));
    }
}

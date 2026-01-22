using System.IO;
using System.Text;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.Serialization;
using Xunit;

namespace NonSilo.Tests.Serialization;

public class OrleansJsonSerializerStreamTests
{
    [Fact]
    public void StreamRoundTrip_SerializesAndDeserializes()
    {
        var serializer = new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()));
        var payload = new TestPayload { Name = "test", Value = 42 };

        using var stream = new MemoryStream();
        serializer.Serialize(payload, typeof(TestPayload), stream);
        stream.Position = 0;

        var result = (TestPayload)serializer.Deserialize(typeof(TestPayload), stream);

        Assert.NotNull(result);
        Assert.Equal(payload.Name, result.Name);
        Assert.Equal(payload.Value, result.Value);
    }

    [Fact]
    public void Deserialize_EmptyStream_ReturnsNull()
    {
        var serializer = new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()));

        using var stream = new MemoryStream();
        var result = serializer.Deserialize(typeof(TestPayload), stream);

        Assert.Null(result);
    }

    [Fact]
    public void StreamRoundTrip_AllowsNull()
    {
        var serializer = new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()));

        using var stream = new MemoryStream();
        serializer.Serialize(null, typeof(TestPayload), stream);
        stream.Position = 0;

        var result = serializer.Deserialize(typeof(TestPayload), stream);

        Assert.Null(result);
    }

    [Fact]
    public void Deserialize_InvalidJson_Throws()
    {
        var serializer = new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()));

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{invalid"));

        Assert.Throws<JsonReaderException>(() => serializer.Deserialize(typeof(TestPayload), stream));
    }

    private sealed class TestPayload
    {
        public string Name { get; set; }
        public int Value { get; set; }
    }
}

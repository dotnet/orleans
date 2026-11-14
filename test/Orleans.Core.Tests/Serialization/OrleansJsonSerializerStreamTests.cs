using System.IO;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Orleans.GrainReferences;
using Orleans.Serialization;
using Xunit;

namespace NonSilo.Tests.Serialization;

public class OrleansJsonSerializerStreamTests
{
    [Fact]
    public void ConstructorRejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>("options", () => new OrleansJsonSerializer(null!));
    }

    [Fact]
    public void NewtonsoftConvertersRejectNullWriters()
    {
        var serializer = JsonSerializer.CreateDefault();
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var referenceActivator = new GrainReferenceActivator(serviceProvider, []);
        Action[] writeJson =
        [
            () => new IPAddressConverter().WriteJson(null!, null, serializer),
            () => new GrainIdConverter().WriteJson(null!, null, serializer),
            () => new Orleans.Serialization.ActivationIdConverter().WriteJson(null!, null, serializer),
            () => new SiloAddressJsonConverter().WriteJson(null!, null, serializer),
            () => new MembershipVersionJsonConverter().WriteJson(null!, null, serializer),
            () => new UniqueKeyConverter().WriteJson(null!, null, serializer),
            () => new IPEndPointConverter().WriteJson(null!, null, serializer),
            () => new GrainReferenceJsonConverter(referenceActivator).WriteJson(null!, null, serializer)
        ];

        foreach (var write in writeJson)
        {
            Assert.Throws<ArgumentNullException>("writer", write);
        }
    }

    [Fact]
    public void NewtonsoftConvertersWriteNullValuesAsJsonNull()
    {
        var serializer = JsonSerializer.CreateDefault();
        Action<JsonWriter>[] writeJson =
        [
            writer => new IPAddressConverter().WriteJson(writer, null, serializer),
            writer => new GrainIdConverter().WriteJson(writer, null, serializer),
            writer => new Orleans.Serialization.ActivationIdConverter().WriteJson(writer, null, serializer),
            writer => new SiloAddressJsonConverter().WriteJson(writer, null, serializer),
            writer => new MembershipVersionJsonConverter().WriteJson(writer, null, serializer),
            writer => new UniqueKeyConverter().WriteJson(writer, null, serializer),
            writer => new IPEndPointConverter().WriteJson(writer, null, serializer)
        ];

        foreach (var write in writeJson)
        {
            using var textWriter = new StringWriter();
            using var writer = new JsonTextWriter(textWriter);
            write(writer);
            writer.Flush();
            Assert.Equal("null", textWriter.ToString());
        }
    }

    [Fact]
    public void NewtonsoftConvertersRejectNullReaders()
    {
        var serializer = JsonSerializer.CreateDefault();

        Assert.Throws<ArgumentNullException>("reader", () => new Orleans.Serialization.ActivationIdConverter().ReadJson(null!, typeof(object), null, serializer));
        Assert.Throws<ArgumentNullException>("reader", () => new SiloAddressJsonConverter().ReadJson(null!, typeof(object), null, serializer));
        Assert.Throws<ArgumentNullException>("reader", () => new MembershipVersionJsonConverter().ReadJson(null!, typeof(object), null, serializer));
    }

    [Fact]
    public void IPEndPointConverterRejectsNullSerializer()
    {
        using var textWriter = new StringWriter();
        using var writer = new JsonTextWriter(textWriter);

        Assert.Throws<ArgumentNullException>("serializer", () => new IPEndPointConverter().WriteJson(writer, null, null!));
    }

    [Fact]
    public void StreamRoundTrip_SerializesAndDeserializes()
    {
        var serializer = new OrleansJsonSerializer(Options.Create(new OrleansJsonSerializerOptions()));
        var payload = new TestPayload { Name = "test", Value = 42 };

        using var stream = new MemoryStream();
        serializer.Serialize(payload, typeof(TestPayload), stream);
        stream.Position = 0;

        TestPayload? result = (TestPayload?)serializer.Deserialize(typeof(TestPayload), stream);

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
        public string? Name { get; set; }
        public int Value { get; set; }
    }
}

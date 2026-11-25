using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace UnitTests.Serialization;

[TestCategory("Serialization")]
public class IpJsonConverterTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void IPAddressJsonConverterRejectsNullWriteInputs()
    {
        var converter = new IPAddressJsonConverter();
        var address = IPAddress.Parse("192.0.2.1");
        Assert.Throws<ArgumentNullException>("writer", () => converter.Write(null!, address, null!));

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        Assert.Throws<ArgumentNullException>("value", () => converter.Write(writer, null!, null!));
        Assert.Equal(0, writer.BytesPending);

        writer.WriteStartObject();
        var bytesPending = writer.BytesPending;
        Assert.Throws<ArgumentNullException>("writer", () => converter.WriteAsPropertyName(null!, address, null!));
        Assert.Throws<ArgumentNullException>("value", () => converter.WriteAsPropertyName(writer, null!, null!));
        Assert.Equal(bytesPending, writer.BytesPending);
        writer.WriteEndObject();
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void IPAddressJsonConverterPreservesAddressAndPropertyJson()
    {
        var converter = new IPAddressJsonConverter();
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        (IPAddress Address, string Json)[] cases =
        [
            (IPAddress.Parse("192.0.2.1"), "\"192.0.2.1\""),
            (IPAddress.Parse("2001:db8::1"), "\"2001:db8::1\""),
        ];

        foreach (var (address, json) in cases)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                converter.Write(writer, address, null!);
            }

            Assert.Equal(json, Encoding.UTF8.GetString(buffer.WrittenSpan));
            Assert.Equal(json, JsonSerializer.Serialize(address, options));
            Assert.Equal(address, JsonSerializer.Deserialize<IPAddress>(json, options));

            var propertyJson = "{" + json + ":7}";
            var entry = Assert.Single(JsonSerializer.Deserialize<Dictionary<IPAddress, int>>(propertyJson, options)!);
            Assert.Equal(address, entry.Key);
            Assert.Equal(7, entry.Value);
        }

        Assert.Equal("null", JsonSerializer.Serialize<IPAddress?>(null, options));
        Assert.Null(JsonSerializer.Deserialize<IPAddress?>("null", options));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void IPEndPointJsonConverterRejectsNullWriteInputs()
    {
        var converter = new IPEndPointJsonConverter();
        var endpoint = new IPEndPoint(IPAddress.Loopback, 1234);
        Assert.Throws<ArgumentNullException>("writer", () => converter.Write(null!, endpoint, null!));

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        Assert.Throws<ArgumentNullException>("value", () => converter.Write(writer, null!, null!));
        Assert.Equal(0, writer.BytesPending);

        writer.WriteStartObject();
        var bytesPending = writer.BytesPending;
        Assert.Throws<ArgumentNullException>("writer", () => converter.WriteAsPropertyName(null!, endpoint, null!));
        Assert.Throws<ArgumentNullException>("value", () => converter.WriteAsPropertyName(writer, null!, null!));
        Assert.Equal(bytesPending, writer.BytesPending);
        writer.WriteEndObject();
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [Fact, TestCategory("BVT")]
    public void IPEndPointJsonConverterPreservesEndpointAndPropertyJson()
    {
        var converter = new IPEndPointJsonConverter();
        var options = new JsonSerializerOptions();
        options.Converters.Add(converter);
        (IPEndPoint Endpoint, string Json)[] cases =
        [
            (new IPEndPoint(IPAddress.Parse("192.0.2.1"), 1234), "\"192.0.2.1:1234\""),
            (new IPEndPoint(IPAddress.Parse("2001:db8::1"), 4321), "\"[2001:db8::1]:4321\""),
        ];

        foreach (var (endpoint, json) in cases)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                converter.Write(writer, endpoint, null!);
            }

            Assert.Equal(json, Encoding.UTF8.GetString(buffer.WrittenSpan));
            Assert.Equal(json, JsonSerializer.Serialize(endpoint, options));
            Assert.Equal(endpoint, JsonSerializer.Deserialize<IPEndPoint>(json, options));

            var propertyJson = "{" + json + ":7}";
            var entry = Assert.Single(JsonSerializer.Deserialize<Dictionary<IPEndPoint, int>>(propertyJson, options)!);
            Assert.Equal(endpoint, entry.Key);
            Assert.Equal(7, entry.Value);
        }

        Assert.Equal("null", JsonSerializer.Serialize<IPEndPoint?>(null, options));
        Assert.Null(JsonSerializer.Deserialize<IPEndPoint?>("null", options));
    }
}

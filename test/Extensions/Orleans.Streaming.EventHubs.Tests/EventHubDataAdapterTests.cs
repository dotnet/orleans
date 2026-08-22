using Azure.Messaging.EventHubs;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Providers.Streams.Common;
using Orleans.Serialization;
using Orleans.Streaming.EventHubs;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests;

public class EventHubDataAdapterTests
{
    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Theory, TestCategory("BVT"), TestCategory("Streaming")]
    [InlineData(false)]
    [InlineData(true)]
    public void SerializeProperties_ReturnsEmptyForNoApplicationProperties(bool includeStreamNamespace)
    {
        var eventData = new EventData();
        if (includeStreamNamespace)
        {
            eventData.SetStreamNamespaceProperty("stream-namespace");
        }

        Assert.Empty(eventData.SerializeProperties(CreateSerializer()));
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void SerializeProperties_RoundTripsApplicationProperties()
    {
        var serializer = CreateSerializer();
        var eventData = new EventData();
        eventData.SetStreamNamespaceProperty("stream-namespace");
        eventData.Properties["number"] = 42;
        eventData.Properties["text"] = "value";

        var bytes = eventData.SerializeProperties(serializer);
        var properties = new ArraySegment<byte>(bytes).DeserializeProperties(serializer);

        Assert.Equal(2, properties.Count);
        Assert.Equal(42, properties["number"]);
        Assert.Equal("value", properties["text"]);
        Assert.DoesNotContain("StreamNamespace", properties);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void DeserializeProperties_EmptyRepresentationReturnsMutableDictionary()
    {
        var properties = new ArraySegment<byte>([]).DeserializeProperties(CreateSerializer());

        Assert.Empty(properties);
        properties.Add("key", "value");
        Assert.Equal("value", properties["key"]);
    }

    [TestSuite("BVT")]
    [TestProvider("None")]
    [TestArea("Streaming")]
    [Fact, TestCategory("BVT"), TestCategory("Streaming")]
    public void GetOffset_ThrowsForMissingOffset()
    {
        var segment = new ArraySegment<byte>(new byte[sizeof(int)]);
        var writerOffset = 0;
        SegmentBuilder.Append(segment, ref writerOffset, (string?)null);

        var adapter = new EventHubDataAdapter(null!);
        var cachedMessage = new CachedMessage { Segment = segment };

        var exception = Assert.Throws<InvalidOperationException>(() => adapter.GetOffset(cachedMessage));
        Assert.Equal("Cached Event Hub message is missing its offset.", exception.Message);
    }

    private static Serializer CreateSerializer() => new ServiceCollection()
        .AddSerializer()
        .BuildServiceProvider()
        .GetRequiredService<Serializer>();
}

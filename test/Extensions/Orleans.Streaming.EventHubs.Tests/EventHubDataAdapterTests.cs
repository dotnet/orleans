using Orleans.Providers.Streams.Common;
using Orleans.Streaming.EventHubs;
using TestExtensions;
using Xunit;

namespace ServiceBus.Tests;

public class EventHubDataAdapterTests
{
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
}

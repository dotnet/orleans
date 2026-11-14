using System;
using Orleans.Configuration;
using Orleans.Streams;
using Xunit;

namespace Tester.StreamingTests;

public class HashRingBasedStreamQueueMapperTests
{
    [Fact]
    public void ConstructorRejectsNullOptions()
    {
        Assert.Throws<ArgumentNullException>(
            "options",
            () => new HashRingBasedStreamQueueMapper(null!, "provider"));
    }

    [Fact]
    public void GetQueuesForRangeRejectsNullRange()
    {
        var mapper = new HashRingBasedStreamQueueMapper(
            new HashRingStreamQueueMapperOptions { TotalQueueCount = 1 },
            "provider");

        Assert.Throws<ArgumentNullException>("range", () => mapper.GetQueuesForRange(null!));
    }

    [Fact]
    public void PartitionedMapperConstructorRejectsNullPartitionIds()
    {
        Assert.Throws<ArgumentNullException>(
            "partitionIds",
            () => new HashRingBasedPartitionedStreamQueueMapper(null!, "provider"));
    }
}

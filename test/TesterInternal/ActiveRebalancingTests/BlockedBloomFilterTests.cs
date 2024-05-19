using Orleans.Runtime.Placement.Rebalancing;
using Xunit;

namespace UnitTests.ActiveRebalancingTests;

public class BlockedBloomFilterTests
{
    [Fact]
    public void AddAndCheck()
    {
        var bloomFilter = new BlockedBloomFilter(100, 0.01);
        var sample = new GrainId(GrainType.Create("type"), IdSpan.Create("key"));
        bloomFilter.Add(sample);
        Assert.True(bloomFilter.Contains(sample));
    }

    [Fact]
    public void DoesNotContainSome()
    {
        var bloomFilter = new BlockedBloomFilter(100, 0.01);
        var sample = new GrainId(GrainType.Create("type"), IdSpan.Create("key"));
        Assert.False(bloomFilter.Contains(sample));
    }
}

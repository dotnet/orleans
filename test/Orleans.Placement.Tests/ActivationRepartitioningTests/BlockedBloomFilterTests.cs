using Orleans.Runtime.Placement.Repartitioning;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

/// <summary>
/// Tests for the blocked bloom filter used in activation repartitioning.
/// </summary>
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

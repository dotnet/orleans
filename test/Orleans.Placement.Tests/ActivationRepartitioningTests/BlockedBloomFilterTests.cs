using Orleans.Runtime.Placement.Repartitioning;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

#nullable enable

public class BlockedBloomFilterTests
{
    private readonly GrainId _grainId = GrainId.Create("test", "key");

    [Fact]
    public void AddAndCheck()
    {
        var filter = new BlockedBloomFilter(100, 0.01);

        filter.Add(_grainId);

        Assert.True(filter.Contains(_grainId));
    }

    [Fact]
    public void DoesNotContainSome()
    {
        var filter = new BlockedBloomFilter(100, 0.01);
        Assert.False(filter.Contains(_grainId));
    }
}

public class AnchoredGrainsFilterTests
{
    private readonly GrainId _grainId = GrainId.Create("test", "key");

    private static AnchoredGrainsFilter CreateFilter(int generations = 2) => new(100, 0.01, generations);

    [Fact]
    public void AddAndCheck()
    {
        var filter = CreateFilter();

        filter.Add(_grainId);

        Assert.True(filter.Contains(_grainId));
    }

    [Fact]
    public void Rotate_RetainsGrains_AfterFirstCycle()
    {
        var filter = CreateFilter();

        filter.Add(_grainId);
        filter.Rotate(); // Gen 0 -> Gen 1

        Assert.True(filter.Contains(_grainId));
    }

    [Fact]
    public void Rotate_DropsGrains_AfterAllCycles_2Gen()
    {
        var filter = CreateFilter(generations: 2);
        filter.Add(_grainId);

        filter.Rotate(); // Gen 0 -> Gen 1
        Assert.True(filter.Contains(_grainId));

        filter.Rotate(); // Gen 1 -> Gone
        Assert.False(filter.Contains(_grainId));
    }

    [Fact]
    public void Rotate_DropsGrains_AfterAllCycles_3Gen()
    {
        var filter = CreateFilter(generations: 3);
        filter.Add(_grainId);

        filter.Rotate(); // Gen 0 -> Gen 1
        Assert.True(filter.Contains(_grainId));

        filter.Rotate(); // Gen 1 -> Gen 2
        Assert.True(filter.Contains(_grainId));

        filter.Rotate(); // Gen 2 -> Gone
        Assert.False(filter.Contains(_grainId));
    }

    [Fact]
    public void ContinuousActivity_KeepsGrainsAnchored()
    {
        var filter = CreateFilter(generations: 3);

        foreach (var _ in Enumerable.Range(0, 10))
        {
            filter.Add(_grainId); // A hot grain
            filter.Rotate();
            Assert.True(filter.Contains(_grainId));
        }
    }

    [Fact]
    public void Reset_ClearsAllFilters()
    {
        var filter = CreateFilter(generations: 3);

        filter.Add(_grainId);
        filter.Reset();

        Assert.False(filter.Contains(_grainId)); // None of the filters should contain it.
    }

    [Fact]
    public void Rotate_CalledManyTimes_DoesNotThrowOutOfBounds()
    {
        var filter = CreateFilter(generations: 3);

        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                filter.Rotate();
            }
        });

        Assert.Null(exception);
    }
}

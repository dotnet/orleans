using TestExtensions;
using UnitTests.GrainInterfaces;
using Xunit;

namespace DefaultCluster.Tests.Serialization;

[TestCategory("Serialization"), TestCategory("BVT")]
public class CollectionExpressionGrainTests : HostedTestClusterEnsureDefaultStarted
{
    public CollectionExpressionGrainTests(DefaultClusterFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task CollectionExpression_IEnumerable_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetEnumerable();

        Assert.Equal([1, 2, 3], result);
    }

    [Fact]
    public async Task CollectionExpression_IReadOnlyList_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetReadOnlyList();

        Assert.Equal([10, 20, 30], result);
    }

    [Fact]
    public async Task CollectionExpression_IList_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetList();

        Assert.Equal([100, 200, 300], result);
    }

    [Fact]
    public async Task CollectionExpression_IReadOnlyCollection_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetReadOnlyCollection();

        Assert.Equal([4, 5, 6], result);
    }

    [Fact]
    public async Task CollectionExpression_ICollection_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetCollection();

        Assert.Equal([40, 50, 60], result);
    }

    [Fact]
    public async Task CollectionExpression_ISet_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetSet();

        Assert.True(result.SetEquals([7, 8, 9]));
    }

    [Fact]
    public async Task CollectionExpression_IReadOnlySet_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetReadOnlySet();

        Assert.True(result.SetEquals([70, 80, 90]));
    }

    [Fact]
    public async Task CollectionExpression_IDictionary_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetDictionary();

        Assert.Equal(3, result.Count);
        Assert.Equal(1, result["alpha"]);
        Assert.Equal(2, result["beta"]);
        Assert.Equal(3, result["gamma"]);
    }

    [Fact]
    public async Task CollectionExpression_IReadOnlyDictionary_RoundTrips()
    {
        var grain = GetGrain();

        var result = await grain.GetReadOnlyDictionary();

        Assert.Equal(3, result.Count);
        Assert.Equal(10, result["x"]);
        Assert.Equal(20, result["y"]);
        Assert.Equal(30, result["z"]);
    }

    private ICollectionExpressionGrain GetGrain() => GrainFactory.GetGrain<ICollectionExpressionGrain>(GetRandomGrainId());
}

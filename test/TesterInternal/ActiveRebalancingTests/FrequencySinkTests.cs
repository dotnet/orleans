using Orleans.Placement.Rebalancing;
using Orleans.Runtime;
using Orleans.Runtime.Placement.Rebalancing;
using Xunit;

namespace UnitTests.ActiveRebalancingTests;

[Alias("UnitTests.ActiveRebalancingTests.IMyGrain")]
public interface IMyGrain : IGrainWithStringKey
{
    [Alias("GetValue")]
    Task<T> GetValue<T>();

    [Alias("GetValue1")]
    Task<T> GetValue<T, H>();
}

[Alias("UnitTests.ActiveRebalancingTests.IMyGrain`1")]
public interface IMyGrain<T> : IGrainWithStringKey
{
    Task<T> GetValue();
}


[TestCategory("Functional"), TestCategory("ActiveRebalancing")]
public class FrequencySinkTests
{
    private readonly GrainId id_A = GrainId.Create("A", Guid.NewGuid().ToString());
    private readonly GrainId id_B = GrainId.Create("B", Guid.NewGuid().ToString());
    private readonly GrainId id_C = GrainId.Create("C", Guid.NewGuid().ToString());
    private readonly GrainId id_D = GrainId.Create("D", Guid.NewGuid().ToString());
    private readonly GrainId id_E = GrainId.Create("E", Guid.NewGuid().ToString());
    private readonly GrainId id_F = GrainId.Create("F", Guid.NewGuid().ToString());

    [Fact]
    public void Add_ShouldIncrementCounter_WhenEdgeIsAdded()
    {
        var sink = new FrequencySink(capacity: 10);
        var edge = new CommEdge(new(id_A, SiloAddress.Zero, true), new(id_B, SiloAddress.Zero, true));

        sink.Add(edge);

        var counters = sink.Counters.ToList();

        Assert.Single(counters);
        Assert.Equal(1u, counters[0].Value);
        Assert.Equal(edge, counters[0].Edge);
    }

    [Fact]
    public void Add_ShouldUpdateExistingCounter_WhenSameEdgeIsAddedAgain()
    {
        var sink = new FrequencySink(capacity: 10);
        var edge = new CommEdge(new(id_A, SiloAddress.Zero, true), new(id_B, SiloAddress.Zero, true));

        sink.Add(edge);
        sink.Add(edge);

        var counters = sink.Counters.ToList();

        Assert.Single(counters);
        Assert.Equal(2u, counters[0].Value);
        Assert.Equal(edge, counters[0].Edge);
    }

    [Fact]
    public void Add_ShouldRemoveMinCounter_WhenCapacityIsReached()
    {
        var sink = new FrequencySink(capacity: 2);

        var edge1 = new CommEdge(new(id_A, SiloAddress.Zero, true), new(id_B, SiloAddress.Zero, true));
        var edge2 = new CommEdge(new(id_C, SiloAddress.Zero, true), new(id_D, SiloAddress.Zero, true));

        sink.Add(edge1);
        sink.Add(edge1);
        sink.Add(edge2);

        Assert.Equal(2, sink.Counters.Count);

        var edge3 = new CommEdge(new(id_E, SiloAddress.Zero, true), new(id_F, SiloAddress.Zero, true));
        sink.Add(edge3);  // should remove the minimum counter (edge2) since capacity is 2

        var counters = sink.Counters.ToList();

        Assert.Equal(2, counters.Count);
        Assert.DoesNotContain(counters, c => c.Edge == edge2);
    }

    [Fact]
    public void Remove_ShouldRemoveCounter_WhenEdgeIsRemoved()
    {
        var sink = new FrequencySink(capacity: 10);
        var edge = new CommEdge(new(id_A, SiloAddress.Zero, true), new(id_B, SiloAddress.Zero, true));

        sink.Add(edge);
        sink.Remove(edge.Source.Id.GetUniformHashCode(), edge.Target.Id.GetUniformHashCode());

        Assert.Empty(sink.Counters);
    }
}
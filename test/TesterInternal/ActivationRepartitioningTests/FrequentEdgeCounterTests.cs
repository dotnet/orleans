using Orleans.Placement.Repartitioning;
using Orleans.Runtime.Placement.Repartitioning;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

[Alias("UnitTests.ActivationRepartitioningTests.IMyPartitionableGrain")]
public interface IMyPartitionableGrain : IGrainWithStringKey
{
    [Alias("GetValue")]
    Task<T> GetValue<T>();

    [Alias("GetValue1")]
    Task<T> GetValue<T, H>();
}

[Alias("UnitTests.ActivationRepartitioningTests.IMyGrain`1")]
public interface IMyActiveBalancingGrain<T> : IGrainWithStringKey
{
    Task<T> GetValue();
}

/// <summary>
/// Tests for the frequent edge counter used to track communication patterns in activation repartitioning.
/// </summary>
[TestCategory("Functional"), TestCategory("ActivationRepartitioning")]
public class FrequentEdgeCounterTests
{
    private static readonly GrainId Id_A = GrainId.Create("A", Guid.NewGuid().ToString());
    private static readonly GrainId Id_B = GrainId.Create("B", Guid.NewGuid().ToString());
    private static readonly GrainId Id_C = GrainId.Create("C", Guid.NewGuid().ToString());
    private static readonly GrainId Id_D = GrainId.Create("D", Guid.NewGuid().ToString());
    private static readonly GrainId Id_E = GrainId.Create("E", Guid.NewGuid().ToString());
    private static readonly GrainId Id_F = GrainId.Create("F", Guid.NewGuid().ToString());

    [Fact]
    public void Add_ShouldIncrementCounter_WhenEdgeIsAdded()
    {
        var sink = new FrequentEdgeCounter(capacity: 10);
        var edge = new Edge(new(Id_A, SiloAddress.Zero, true), new(Id_B, SiloAddress.Zero, true));

        sink.Add(edge);

        var counters = sink.Elements.ToList();

        Assert.Single(counters);
        Assert.Equal(1u, counters[0].Count);
        Assert.Equal(edge, counters[0].Element);
    }

    [Fact]
    public void Add_ShouldUpdateExistingCounter_WhenSameEdgeIsAddedAgain()
    {
        var sink = new FrequentEdgeCounter(capacity: 10);
        var edge = new Edge(new(Id_A, SiloAddress.Zero, true), new(Id_B, SiloAddress.Zero, true));

        sink.Add(edge);
        sink.Add(edge);

        var counters = sink.Elements.ToList();

        Assert.Single(counters);
        Assert.Equal(2u, counters[0].Count);
        Assert.Equal(edge, counters[0].Element);
    }

    [Fact]
    public void Add_ShouldRemoveMinCounter_WhenCapacityIsReached()
    {
        var sink = new FrequentEdgeCounter(capacity: 2);

        var edge1 = new Edge(new(Id_A, SiloAddress.Zero, true), new(Id_B, SiloAddress.Zero, true));
        var edge2 = new Edge(new(Id_C, SiloAddress.Zero, true), new(Id_D, SiloAddress.Zero, true));

        sink.Add(edge1);
        sink.Add(edge1);
        sink.Add(edge2);

        Assert.Equal(2, sink.Count);

        var edge3 = new Edge(new(Id_E, SiloAddress.Zero, true), new(Id_F, SiloAddress.Zero, true));
        sink.Add(edge3);  // should remove the minimum counter (edge2) since capacity is 2

        var counters = sink.Elements.ToList();

        Assert.Equal(2, counters.Count);
        Assert.DoesNotContain(counters, c => c.Element == edge2);
    }

    [Fact]
    public void Remove_ShouldRemoveCounter_WhenEdgeIsRemoved()
    {
        var sink = new FrequentEdgeCounter(capacity: 10);
        var edge = new Edge(new(Id_A, SiloAddress.Zero, true), new(Id_B, SiloAddress.Zero, true));

        sink.Add(edge);
        sink.Remove(edge);

        Assert.Empty(sink.Elements);
    }
}

using Orleans.Placement.Repartitioning;
using Orleans.Runtime;
using Orleans.Runtime.Placement.Repartitioning;
using Xunit;

namespace UnitTests.ActivationRepartitioningTests;

[TestCategory("Functional"), TestCategory("ActivationRepartitioning")]
public class DeactivatedGrainQueueTests
{
    private static readonly GrainId Id_A = GrainId.Create("A", Guid.NewGuid().ToString());
    private static readonly GrainId Id_B = GrainId.Create("B", Guid.NewGuid().ToString());
    private static readonly GrainId Id_C = GrainId.Create("C", Guid.NewGuid().ToString());
    private static readonly GrainId Id_D = GrainId.Create("D", Guid.NewGuid().ToString());
    private static readonly GrainId Id_E = GrainId.Create("E", Guid.NewGuid().ToString());

    [Fact]
    public void DeactivatedGrainQueue_DrainsToDistinctAffectedGrains()
    {
        var queue = new ActivationRepartitioner.DeactivatedGrainQueue();
        var observer = (IActivationWorkingSetObserver)queue;
        var member = new TestActivationWorkingSetMember(Id_A);

        observer.OnDeactivated(member);
        observer.OnDeactivated(member);

        Assert.Equal(2, queue.Count);

        HashSet<GrainId> affected = [];
        queue.DrainTo(affected);

        Assert.Single(affected);
        Assert.Contains(Id_A, affected);
        Assert.Equal(0, queue.Count);

        observer.OnDeactivated(member);

        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void DeactivatedGrainQueue_IsBounded()
    {
        var queue = new ActivationRepartitioner.DeactivatedGrainQueue();

        for (var i = 0; i < ActivationRepartitioner.DeactivatedGrainQueue.MaxTrackedDeactivatedGrains + 100; i++)
        {
            queue.Add(GrainId.Create("bounded-grain", i.ToString()));
        }

        Assert.Equal(ActivationRepartitioner.DeactivatedGrainQueue.MaxTrackedDeactivatedGrains, queue.Count);
        Assert.False(queue.Add(GrainId.Create("bounded-grain", "overflow")));
    }

    [Fact]
    public async Task PruneAffectedEdges_RemovesEdgesTouchingAffectedGrains()
    {
        var edgeCounter = new FrequentEdgeCounter(capacity: 10);
        var affectedAsSource = new Edge(new(Id_A, SiloAddress.Zero, true), new(Id_B, SiloAddress.Zero, true));
        var affectedAsTarget = new Edge(new(Id_B, SiloAddress.Zero, true), new(Id_A, SiloAddress.Zero, true));
        var unaffected = new Edge(new(Id_C, SiloAddress.Zero, true), new(Id_D, SiloAddress.Zero, true));
        var otherUnaffected = new Edge(new(Id_D, SiloAddress.Zero, true), new(Id_E, SiloAddress.Zero, true));

        edgeCounter.Add(affectedAsSource);
        edgeCounter.Add(affectedAsTarget);
        edgeCounter.Add(unaffected);
        edgeCounter.Add(otherUnaffected);

        await ActivationRepartitioner.PruneAffectedEdges(edgeCounter, [Id_A], anchoredFilter: null);

        var remainingEdges = edgeCounter.Elements.Select(element => element.Element).ToList();
        Assert.DoesNotContain(affectedAsSource, remainingEdges);
        Assert.DoesNotContain(affectedAsTarget, remainingEdges);
        Assert.Contains(unaffected, remainingEdges);
        Assert.Contains(otherUnaffected, remainingEdges);
    }

    private sealed class TestActivationWorkingSetMember(GrainId grainId) : IActivationWorkingSetMember, IGrainContext
    {
        public GrainId GrainId { get; } = grainId;

        public GrainReference GrainReference => throw new NotImplementedException();
        public object GrainInstance => throw new NotImplementedException();
        public ActivationId ActivationId => throw new NotImplementedException();
        public GrainAddress Address => throw new NotImplementedException();
        public IServiceProvider ActivationServices => throw new NotImplementedException();
        public IGrainLifecycle ObservableLifecycle => throw new NotImplementedException();
        public IWorkItemScheduler Scheduler => throw new NotImplementedException();
        public Task Deactivated => Task.CompletedTask;

        public bool IsCandidateForRemoval(bool wouldRemove) => false;
        public void SetComponent<TComponent>(TComponent value) where TComponent : class => throw new NotImplementedException();
        public void ReceiveMessage(object message) => throw new NotImplementedException();
        public void Activate(Dictionary<string, object> requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Deactivate(DeactivationReason deactivationReason, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public void Rehydrate(IRehydrationContext context) => throw new NotImplementedException();
        public void Migrate(Dictionary<string, object> requestContext, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public object GetTarget() => throw new NotImplementedException();
        public object GetComponent(Type componentType) => throw new NotImplementedException();
        public bool Equals(IGrainContext other) => ReferenceEquals(this, other);
    }
}

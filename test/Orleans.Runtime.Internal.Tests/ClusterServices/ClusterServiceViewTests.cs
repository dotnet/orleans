using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using Orleans.Serialization;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class ClusterServiceViewTests
{
    [Fact]
    public void ProviderEpochOrdersBeforeProviderRevision()
    {
        var oldAuthority = new ClusterServiceViewId(1, new(long.MaxValue));
        var newAuthority = new ClusterServiceViewId(2, new(0));
        var nextRevision = new ClusterServiceViewId(2, new(1));

        Assert.True(oldAuthority < newAuthority);
        Assert.True(newAuthority > oldAuthority);
        Assert.True(newAuthority < nextRevision);
        Assert.Equal(newAuthority, new ClusterServiceViewId(2, new(0)));
        Assert.NotEqual(newAuthority, new ClusterServiceViewId(1, new(0)));
        Assert.Equal(0, newAuthority.CompareTo(new ClusterServiceViewId(2, new(0))));
    }

    [Fact]
    public void AuthoritativePredecessorSupportsRevisionGapsAndProviderChanges()
    {
        var topology = EmptyTopology();
        var previous = new MetadataView(new(1, new(50)), null, topology, "old");
        var next = new MetadataView(new(1, new(100)), previous.Id, topology, "updated");
        var newProvider = new MetadataView(new(2, new(0)), next.Id, topology, "new authority");

        Assert.True(next.IsDirectSuccessorOf(previous));
        Assert.True(newProvider.IsDirectSuccessorOf(next));
        Assert.False(newProvider.IsDirectSuccessorOf(previous));
        Assert.Same(topology, next.Topology);
        Assert.Equal("updated", next.Configuration);
        Assert.Equal("new authority", newProvider.Configuration);
    }

    [Fact]
    public void UnknownPredecessorDoesNotImplyHandoffContinuity()
    {
        var previous = new MetadataView(new(1, new(50)), null, EmptyTopology(), "previous");
        var next = new MetadataView(new(1, new(51)), null, previous.Topology, "next");

        Assert.False(next.IsDirectSuccessorOf(previous));
    }

    [Fact]
    public void MembershipViewsUseMembershipRevisionWithinConfiguredEpoch()
    {
        var configuration = new ClusterServiceConfiguration("directory", 1, "ring");
        var first = new MembershipBasedClusterServiceView(Snapshot(10), configuration, Boundaries, providerEpoch: 7);
        var next = new MembershipBasedClusterServiceView(Snapshot(11), configuration, Boundaries, providerEpoch: 7);
        var skipped = new MembershipBasedClusterServiceView(Snapshot(12), configuration, Boundaries, providerEpoch: 7);
        var otherProvider = new MembershipBasedClusterServiceView(Snapshot(11), configuration, Boundaries, providerEpoch: 8);

        Assert.Equal(new ClusterServiceViewId(7, new(10)), first.Id);
        Assert.True(next.IsDirectSuccessorOf(first));
        Assert.False(skipped.IsDirectSuccessorOf(first));
        Assert.False(otherProvider.IsDirectSuccessorOf(first));
        Assert.Same(configuration, first.Configuration);
    }

    [Fact]
    public void UninitializedMembershipViewHasNoPredecessor()
    {
        var view = new MembershipBasedClusterServiceView(
            ClusterMembershipSnapshot.Default, new("service", 1, "ring"), Boundaries);

        Assert.Equal(ClusterServiceViewVersion.MinValue, view.Id.Version);
        Assert.Null(view.PreviousViewId);
        Assert.Empty(view.Topology.Members);
    }

    [Fact]
    public void ViewRejectsPredecessorFromItsOwnOrLaterPosition()
    {
        var id = new ClusterServiceViewId(2, new(5));

        Assert.Throws<ArgumentException>(() => new MetadataView(id, id, EmptyTopology(), "same"));
        Assert.Throws<ArgumentException>(() => new MetadataView(id, new(3, new(0)), EmptyTopology(), "later"));
    }

    [Fact]
    public async Task TransitionOrderingUsesTheWholeProviderScopedIdentity()
    {
        var range = RingRange.Create(100, 200);
        var oldView = new ClusterServiceViewId(1, new(100));
        var newView = new ClusterServiceViewId(2, new(0));
        var coordinator = new PartitionTransitionCoordinator();
        var transition = coordinator.BeginInbound(range, oldView, newView);

        Assert.False(coordinator.IsBlocked(range, oldView));
        Assert.True(coordinator.TryGetBlockingTransition(range, newView, out var wait));
        Assert.True(coordinator.IsBlocked(range, new(2, new(1))));
        Assert.Throws<InvalidOperationException>(() => coordinator.BeginBarrier(range, newView));

        transition.MarkStateInstalled();
        transition.MarkFenced(new(ClusterServiceFencingMode.External, 42));
        transition.Complete();

        await wait;
        Assert.False(coordinator.IsBlocked(range, newView));
        Assert.Throws<ArgumentException>(() => coordinator.BeginInbound(range, newView, oldView));
    }

    [Fact]
    public void GeneratedSerializerPreservesProviderEpochAndRevision()
    {
        using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
        var serializer = services.GetRequiredService<Serializer>();
        var expected = new ClusterServiceViewId(13, new(long.MaxValue));

        var actual = serializer.Deserialize<ClusterServiceViewId>(serializer.SerializeToArray(expected));

        Assert.Equal(expected, actual);
        Assert.Equal(13, actual.ProviderEpoch);
        Assert.Equal(long.MaxValue, actual.Version.Value);
    }

    private static ClusterMembershipSnapshot Snapshot(long version) =>
        new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty, new(version));

    private static uint[] Boundaries(SiloAddress _, int count) =>
        Enumerable.Range(0, count).Select(value => (uint)value).ToArray();

    private static ClusterServiceTopology EmptyTopology() => new([], 1, Boundaries);

    private sealed class MetadataView(
        ClusterServiceViewId id,
        ClusterServiceViewId? previousViewId,
        ClusterServiceTopology topology,
        string configuration) : ClusterServiceView(id, previousViewId, topology)
    {
        public string Configuration { get; } = configuration;
    }
}

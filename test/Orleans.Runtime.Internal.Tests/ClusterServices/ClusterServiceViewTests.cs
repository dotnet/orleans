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
    public void RevisionsAreOrderedOnlyWithinTheirConfiguredAuthority()
    {
        var oldAuthority = new ClusterServiceViewId(1, new(long.MaxValue));
        var newAuthority = new ClusterServiceViewId(2, new(0));
        var nextRevision = new ClusterServiceViewId(2, new(1));

        Assert.Throws<InvalidOperationException>(() => oldAuthority.CompareTo(newAuthority));
        Assert.Throws<InvalidOperationException>(() => newAuthority.CompareTo(oldAuthority));
        Assert.True(newAuthority < nextRevision);
        Assert.Equal(newAuthority, new ClusterServiceViewId(2, new(0)));
        Assert.NotEqual(newAuthority, new ClusterServiceViewId(1, new(0)));
        Assert.Equal(0, newAuthority.CompareTo(new ClusterServiceViewId(2, new(0))));
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
        Assert.True(next.TryGetPredecessor(out var predecessor));
        Assert.Equal(first.Id, predecessor);
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
        Assert.False(view.TryGetPredecessor(out _));
        Assert.Empty(view.Topology.Members);
    }

    [Fact]
    public void MembershipHandoffRequiresTheSameAgreedConfiguration()
    {
        var previous = new MembershipBasedClusterServiceView(Snapshot(5), new("orders", 1, "ring"), Boundaries);
        var next = new MembershipBasedClusterServiceView(Snapshot(6), new("orders", 1, "ring"), Boundaries);
        var changed = new MembershipBasedClusterServiceView(Snapshot(6), new("orders", 2, "ring"), Boundaries);
        Assert.True(next.IsDirectSuccessorOf(previous));
        Assert.False(changed.IsDirectSuccessorOf(previous));
    }

    [Fact]
    public async Task TransitionOrderingUsesTheConfiguredAuthority()
    {
        var range = RingRange.Create(100, 200);
        var oldView = new ClusterServiceViewId(1, new(100));
        var newView = new ClusterServiceViewId(1, new(101));
        var coordinator = new RangeTransitionGateMap<ClusterServiceViewId>();
        var transition = new OwnershipAcquisition<ClusterServiceViewId>(oldView, newView);
        coordinator.Add(range, transition);

        Assert.False(coordinator.IsBlocked(range, oldView));
        Assert.True(coordinator.TryGetBlockingTransition(range, newView, out var wait));
        Assert.True(coordinator.IsBlocked(range, new(1, new(102))));
        Assert.Throws<InvalidOperationException>(() => coordinator.Add(range, new ViewBarrier<ClusterServiceViewId>(newView)));
        Assert.Throws<InvalidOperationException>(() => coordinator.IsBlocked(range, new(2, new(0))));

        transition.MarkStateInstalled();
        transition.MarkFenced(new(ClusterServiceFencingMode.External, 42));
        transition.Complete();

        await wait;
        Assert.False(coordinator.IsBlocked(range, newView));
        Assert.Throws<ArgumentException>(() => new OwnershipAcquisition<ClusterServiceViewId>(newView, oldView));
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

}

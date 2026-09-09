using System.Collections;
using System.Reflection;
using CsCheck;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class TransitionGateMapTests
{
    [Fact]
    public void ResourceMap_UsesExactCompositeIdentityAndRequestView()
    {
        var map = new ResourceTransitionGateMap<ResourceId, long>();
        var resource = new ResourceId("ns", "hub", "group", "0");
        var gate = new OwnershipAcquisition<long>(1, 2);
        map.Add(resource, gate);

        Assert.False(map.IsBlocked(resource, 1));
        Assert.True(map.TryGetBlockingTransition(resource with { }, 2, out var wait));
        Assert.Same(gate.Completion, wait);
        Assert.True(map.IsBlocked(resource, 3));
        Assert.False(map.IsBlocked(resource with { Namespace = "other" }, 3));
        Assert.False(map.IsBlocked(resource with { Hub = "other" }, 3));
        Assert.False(map.IsBlocked(resource with { ConsumerGroup = "other" }, 3));
        Assert.False(map.IsBlocked(resource with { Partition = "1" }, 3));
        Assert.False(map.TryGetBlockingTransition(resource with { Partition = "1" }, 3, out var absent));
        Assert.Same(Task.CompletedTask, absent);
        Assert.Throws<InvalidOperationException>(() => map.Add(resource with { }, new ViewBarrier<long>(2)));
        Assert.Equal(TransitionGateStatus.Pending, gate.Status);
    }

    [Fact]
    public void ResourceMap_RespectsExplicitIdentityComparer()
    {
        var map = new ResourceTransitionGateMap<string, string>(StringComparer.OrdinalIgnoreCase);
        var gate = new ViewBarrier<string>("b");
        map.Add("partition", gate);
        Assert.True(map.TryGetBlockingTransition("PARTITION", "b", out var wait));
        Assert.Same(gate.Completion, wait);
        Assert.False(map.IsBlocked("PARTITION", "a"));
        Assert.Throws<InvalidOperationException>(() => map.Add("PARTITION", new ViewBarrier<string>("b")));
    }

    [Fact]
    public void ResourceMap_DisjointRegistrationsLeaveOtherResourceAssociationsUntouched()
    {
        var map = new ResourceTransitionGateMap<int, long>();
        var entries = (Dictionary<int, List<TransitionGate<long>>>)GetAssociations(map);
        var staleGates = new List<ViewBarrier<long>>();
        for (var resource = 0; resource < 128; resource++)
        {
            var gate = new ViewBarrier<long>(1);
            map.Add(resource, gate);
            gate.Complete();
            staleGates.Add(gate);
        }

        Assert.Equal(128, entries.Count);
        for (var resource = 0; resource < staleGates.Count; resource++)
        {
            Assert.Same(staleGates[resource], Assert.Single(entries[resource]));
            Assert.False(map.IsBlocked(resource, 1));
        }

        var replacement = new ViewBarrier<long>(1);
        map.Add(64, replacement);
        Assert.Equal(128, entries.Count);
        Assert.Same(replacement, Assert.Single(entries[64]));
        Assert.Same(staleGates[63], Assert.Single(entries[63]));
        Assert.Same(staleGates[65], Assert.Single(entries[65]));
        Assert.True(map.TryGetBlockingTransition(64, 1, out var wait));
        Assert.Same(replacement.Completion, wait);

        map.Prune();
        Assert.Equal(64, Assert.Single(entries).Key);
        Assert.Same(replacement, Assert.Single(entries[64]));
    }

    [Fact]
    public async Task ResourceMap_TargetedPruneKeepsFailedGatesAndOtherResourcesUntouched()
    {
        var map = new ResourceTransitionGateMap<string, long>(StringComparer.OrdinalIgnoreCase);
        var entries = (Dictionary<string, List<TransitionGate<long>>>)GetAssociations(map);
        var selected = new ViewBarrier<long>(1);
        var failed = new ViewBarrier<long>(2);
        var other = new ViewBarrier<long>(1);
        map.Add("selected", selected);
        map.Add("selected", failed);
        map.Add("other", other);
        selected.Complete();
        other.Complete();
        var failure = new ApplicationException("must remain blocking");
        failed.Fail(failure);

        map.Prune("SELECTED");
        Assert.Equal(2, entries.Count);
        Assert.Same(failed, Assert.Single(entries["selected"]));
        Assert.Same(other, Assert.Single(entries["other"]));
        Assert.True(map.TryGetBlockingTransition("selected", 2, out var wait));
        Assert.Same(failed.Completion, wait);
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => wait));
        Assert.Throws<InvalidOperationException>(() => map.Add("selected", new ViewBarrier<long>(2)));

        failed.Abort();
        map.Prune("selected");
        Assert.Equal("other", Assert.Single(entries).Key);
        Assert.False(map.IsBlocked("selected", 2));
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => failed.Completion));
        map.Prune("missing");
        Assert.Equal("other", Assert.Single(entries).Key);
        map.Prune();
        Assert.Empty(entries);
    }

    [Fact]
    public void RangeMap_PreservesWrappingEmptyFullAndExclusiveStartInclusiveEnd()
    {
        var map = new RangeTransitionGateMap<long>();
        var range = RingRange.Create(300, 100);
        var gate = new ViewBarrier<long>(2);
        map.Add(range, gate);

        Assert.False(map.IsBlocked(RingRange.Full, 1));
        Assert.False(map.IsBlocked(RingRange.Empty, 3));
        Assert.False(map.IsBlocked(RingRange.FromPoint(300), 3));
        Assert.True(map.IsBlocked(RingRange.FromPoint(301), 2));
        Assert.True(map.IsBlocked(RingRange.FromPoint(0), 2));
        Assert.True(map.IsBlocked(RingRange.FromPoint(uint.MaxValue), 2));
        Assert.True(map.IsBlocked(RingRange.FromPoint(100), 3));
        Assert.False(map.IsBlocked(RingRange.FromPoint(101), 3));
        Assert.False(map.IsBlocked(RingRange.Create(100, 300), 3));
        Assert.True(map.TryGetBlockingTransition(RingRange.Full, 3, out var wait));
        Assert.Same(gate.Completion, wait);
        Assert.Throws<ArgumentException>(() => map.Add(RingRange.Empty, new ViewBarrier<long>(3)));
        Assert.Throws<ArgumentNullException>(() => map.Add(range, null!));
        Assert.Throws<InvalidOperationException>(() => map.Add(RingRange.FromPoint(0), new ViewBarrier<long>(2)));
        map.Add(RingRange.Create(100, 300), new ViewBarrier<long>(2));
        Assert.True(map.IsBlocked(RingRange.FromPoint(300), 2));
    }

    [Fact]
    public void RangeMap_FullRingGateIntersectsEveryPointButNeverTheEmptyRange()
    {
        var map = new RangeTransitionGateMap<long>();
        var gate = new ViewBarrier<long>(2);
        map.Add(RingRange.Full, gate);
        foreach (var point in new[] { 0u, 1u, uint.MaxValue })
        {
            Assert.True(map.TryGetBlockingTransition(RingRange.FromPoint(point), 2, out var wait));
            Assert.Same(gate.Completion, wait);
            Assert.Throws<InvalidOperationException>(() => map.Add(RingRange.FromPoint(point), new ViewBarrier<long>(2)));
        }

        Assert.False(map.IsBlocked(RingRange.Empty, 2));
        gate.Complete();
        Assert.False(map.IsBlocked(RingRange.Full, 2));
    }

    [Fact]
    public void BothMaps_QueryEachCandidateOnceAndStopAtTheFirstBlocker()
    {
        var resourceMap = new ResourceTransitionGateMap<string, CountingViewId>();
        var rangeMap = new RangeTransitionGateMap<CountingViewId>();
        var range = RingRange.Create(100, 200);
        var views = new[] { new CountingViewId(1), new CountingViewId(2), new CountingViewId(3) };
        var gates = views.Select(static view => new ViewBarrier<CountingViewId>(view)).ToArray();
        foreach (var gate in gates)
        {
            resourceMap.Add("r", gate);
            rangeMap.Add(range, gate);
        }

        gates[0].Complete();
        Assert.True(resourceMap.TryGetBlockingTransition("r", views[2], out var resourceWait));
        Assert.Same(gates[1].Completion, resourceWait);
        Assert.Equal(new[] { 1, 1, 0 }, views.Select(static view => view.Comparisons).ToArray());
        foreach (var view in views)
        {
            view.Comparisons = 0;
        }

        Assert.True(rangeMap.TryGetBlockingTransition(range, views[2], out var rangeWait));
        Assert.Same(resourceWait, rangeWait);
        Assert.Equal(new[] { 1, 1, 0 }, views.Select(static view => view.Comparisons).ToArray());
        Assert.Equal(3, AssociationCount(resourceMap));
        Assert.Equal(3, AssociationCount(rangeMap));
    }

    [Fact]
    public async Task BothMaps_ShutdownReleasesSharedGateAssociationsAndPreservesCancellationToken()
    {
        var resourceMap = new ResourceTransitionGateMap<string, long>();
        var rangeMap = new RangeTransitionGateMap<long>();
        var firstRange = RingRange.Create(100, 200);
        var secondRange = RingRange.Create(300, 400);
        var gate = new ViewBarrier<long>(2);
        resourceMap.Add("first", gate);
        resourceMap.Add("second", gate);
        rangeMap.Add(firstRange, gate);
        rangeMap.Add(secondRange, gate);
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();

        resourceMap.AbortAll(shutdown.Token);
        rangeMap.AbortAll(shutdown.Token);
        Assert.False(resourceMap.IsBlocked("first", 2));
        Assert.False(resourceMap.IsBlocked("second", 2));
        Assert.False(rangeMap.IsBlocked(RingRange.Full, 2));
        Assert.Equal(0, AssociationCount(resourceMap));
        Assert.Equal(0, AssociationCount(rangeMap));
        var cancellation = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Completion);
        Assert.Equal(shutdown.Token, cancellation.CancellationToken);
    }

    [Fact]
    public async Task BothMaps_RequeryMultipleBlockersAndPruneOnlyReleasedAssociations()
    {
        var resourceMap = new ResourceTransitionGateMap<string, long>();
        var rangeMap = new RangeTransitionGateMap<long>();
        var range = RingRange.Create(100, 200);
        var first = new ViewBarrier<long>(2);
        var second = new ViewBarrier<long>(3);
        var failed = new ViewBarrier<long>(4);
        var failure = new ApplicationException("retained blocker");
        foreach (var gate in new[] { first, second, failed })
        {
            resourceMap.Add("r", gate);
            rangeMap.Add(range, gate);
        }

        failed.Fail(failure);
        Assert.True(resourceMap.TryGetBlockingTransition("r", 4, out var resourceWait));
        Assert.True(rangeMap.TryGetBlockingTransition(range, 4, out var rangeWait));
        Assert.Same(first.Completion, resourceWait);
        Assert.Same(resourceWait, rangeWait);

        first.Complete();
        await resourceWait;
        Assert.True(resourceMap.TryGetBlockingTransition("r", 4, out resourceWait));
        Assert.True(rangeMap.TryGetBlockingTransition(range, 4, out rangeWait));
        Assert.Same(second.Completion, resourceWait);
        Assert.Same(resourceWait, rangeWait);
        resourceMap.Prune();
        rangeMap.Prune();
        Assert.False(resourceMap.IsBlocked("r", 2));
        Assert.False(rangeMap.IsBlocked(range, 2));

        second.Abort();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resourceWait);
        resourceMap.Prune();
        rangeMap.Prune();
        Assert.True(resourceMap.TryGetBlockingTransition("r", 4, out resourceWait));
        Assert.True(rangeMap.TryGetBlockingTransition(range, 4, out rangeWait));
        Assert.Same(failed.Completion, resourceWait);
        Assert.Same(resourceWait, rangeWait);
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => resourceWait));
        Assert.Throws<InvalidOperationException>(() => resourceMap.Add("r", new ViewBarrier<long>(4)));
        Assert.Throws<InvalidOperationException>(() => rangeMap.Add(range, new ViewBarrier<long>(4)));

        resourceMap.AbortAll(TestContext.Current.CancellationToken);
        rangeMap.AbortAll(TestContext.Current.CancellationToken);
        Assert.False(resourceMap.IsBlocked("r", 4));
        Assert.False(rangeMap.IsBlocked(range, 4));
        Assert.Equal(TransitionGateStatus.Aborted, failed.Status);
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() => failed.Completion));
        Assert.Equal(0, AssociationCount(resourceMap));
        Assert.Equal(0, AssociationCount(rangeMap));
    }

    [Fact]
    public void BothMaps_ReadOnlyLookupLeavesPruningToMutations()
    {
        var resourceMap = new ResourceTransitionGateMap<string, long>();
        var rangeMap = new RangeTransitionGateMap<long>();
        var range = RingRange.Create(100, 200);
        var completed = new ViewBarrier<long>(1);
        resourceMap.Add("r", completed);
        rangeMap.Add(range, completed);
        completed.Complete();

        Assert.False(resourceMap.IsBlocked("r", 1));
        Assert.False(rangeMap.TryGetBlockingTransition(range, 1, out _));
        Assert.Equal(1, AssociationCount(resourceMap));
        Assert.Equal(1, AssociationCount(rangeMap));

        var replacement = new ViewBarrier<long>(1);
        resourceMap.Add("r", replacement);
        rangeMap.Add(range, replacement);
        Assert.Equal(1, AssociationCount(resourceMap));
        Assert.Equal(1, AssociationCount(rangeMap));
        Assert.True(resourceMap.TryGetBlockingTransition("r", 1, out var resourceWait));
        Assert.True(rangeMap.TryGetBlockingTransition(range, 1, out var rangeWait));
        Assert.Same(replacement.Completion, resourceWait);
        Assert.Same(resourceWait, rangeWait);

        replacement.Abort();
        resourceMap.Prune();
        rangeMap.Prune();
        Assert.Equal(0, AssociationCount(resourceMap));
        Assert.Equal(0, AssociationCount(rangeMap));
    }

    [Fact]
    public async Task MapsAndResources_AdvanceAndAbortIndependently()
    {
        var firstMap = new ResourceTransitionGateMap<string, long>();
        var secondMap = new ResourceTransitionGateMap<string, long>();
        var acquisition = new OwnershipAcquisition<long>(1, 2);
        var release = new OwnershipRelease<long>(1, 2);
        var other = new ViewBarrier<long>(2);
        firstMap.Add("a", acquisition);
        firstMap.Add("b", other);
        secondMap.Add("a", release);
        acquisition.MarkStateInstalled();
        acquisition.MarkFenced(new(ClusterServiceFencingMode.External, 1));
        acquisition.Complete();
        await acquisition.Completion;

        Assert.False(firstMap.IsBlocked("a", 2));
        Assert.True(firstMap.IsBlocked("b", 2));
        Assert.True(secondMap.IsBlocked("a", 2));
        Assert.Equal(ReleasePhase.Blocking, release.Phase);
        firstMap.AbortAll(TestContext.Current.CancellationToken);
        Assert.Equal(TransitionGateStatus.Aborted, other.Status);
        Assert.Equal(TransitionGateStatus.Pending, release.Status);
        Assert.False(release.Completion.IsCompleted);
        release.MarkDrained();
        release.Complete();
        await release.Completion;
        Assert.False(secondMap.IsBlocked("a", 2));
    }

    [Fact]
    public void CsCheck_RangeMapMatchesIndependentLinearizedIntersectionOracle()
    {
        Gen.Int.Array[24].Sample(
            choices =>
            {
                var mapA = new RangeTransitionGateMap<long>();
                var mapB = new RangeTransitionGateMap<long>();
                var rangeA = RingRange.Create(3_000_000_000, 500_000_000);
                var rangeB = RingRange.Create(1_000_000_000, 2_000_000_000);
                var acquisition = new OwnershipAcquisition<long>(4, 5);
                var release = new OwnershipRelease<long>(6, 7);
                mapA.Add(rangeA, acquisition);
                mapB.Add(rangeB, release);
                foreach (var choice in choices)
                {
                    var raw = unchecked((uint)choice);
                    var statusA = acquisition.Status;
                    var phaseA = acquisition.Phase;
                    var statusB = release.Status;
                    var phaseB = release.Phase;
                    switch (raw % 6)
                    {
                        case 0 when acquisition.Status is TransitionGateStatus.Pending:
                            switch (acquisition.Phase)
                            {
                                case AcquisitionPhase.AwaitingState: acquisition.MarkStateInstalled(); break;
                                case AcquisitionPhase.StateInstalled: acquisition.MarkFenced(new(ClusterServiceFencingMode.External, choice)); break;
                                case AcquisitionPhase.Fenced: acquisition.Complete(); break;
                            }

                            break;
                        case 1: acquisition.Abort(); break;
                        case 2 when release.Status is TransitionGateStatus.Pending:
                            switch (release.Phase)
                            {
                                case ReleasePhase.Blocking: release.MarkDrained(); break;
                                case ReleasePhase.Drained: release.MarkStateRetained(); break;
                                case ReleasePhase.StateRetained: release.Complete(); break;
                            }

                            break;
                        case 3: release.Abort(); break;
                    }

                    if (raw % 6 <= 1)
                    {
                        Assert.Equal(statusB, release.Status);
                        Assert.Equal(phaseB, release.Phase);
                    }
                    else if (raw % 6 <= 3)
                    {
                        Assert.Equal(statusA, acquisition.Status);
                        Assert.Equal(phaseA, acquisition.Phase);
                    }

                    var query = RingRange.Create(raw * 2_654_435_761u,
                        System.Numerics.BitOperations.RotateLeft(raw ^ 0xA5A5_A5A5u, 13));
                    var view = raw % 10;
                    Assert.Equal(acquisition.IsBlocking && view >= 5 && Intersects(rangeA, query), mapA.IsBlocked(query, view));
                    Assert.Equal(release.IsBlocking && view >= 7 && Intersects(rangeB, query), mapB.IsBlocked(query, view));
                    Assert.Equal(acquisition.IsBlocking, !acquisition.Completion.IsCompleted);
                    Assert.Equal(release.IsBlocking, !release.Completion.IsCompleted);
                }
            },
            seed: "partition-transition-isolation-v1",
            iter: 100,
            threads: 1,
            print: static choices => $"choices=[{string.Join(',', choices)}]");
    }

    private static int AssociationCount(object map)
    {
        var entries = GetAssociations(map);
        return entries is IDictionary resources
            ? resources.Values.Cast<ICollection>().Sum(static gates => gates.Count)
            : ((ICollection)entries).Count;
    }

    private static object GetAssociations(object map) =>
        map.GetType().GetField("_transitions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(map)!;

    private static bool Intersects(RingRange left, RingRange right)
    {
        Span<(uint Start, uint End)> leftSegments = stackalloc (uint, uint)[2];
        Span<(uint Start, uint End)> rightSegments = stackalloc (uint, uint)[2];
        var leftCount = Linearize(left, leftSegments);
        var rightCount = Linearize(right, rightSegments);
        for (var i = 0; i < leftCount; i++)
        {
            for (var j = 0; j < rightCount; j++)
            {
                if (leftSegments[i].Start <= rightSegments[j].End && rightSegments[j].Start <= leftSegments[i].End)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int Linearize(RingRange range, Span<(uint Start, uint End)> segments)
    {
        if (range.IsEmpty)
        {
            return 0;
        }

        if (range.IsFull)
        {
            segments[0] = (0, uint.MaxValue);
            return 1;
        }

        if (range.Start < range.End)
        {
            segments[0] = (range.Start + 1, range.End);
            return 1;
        }

        var count = 0;
        if (range.Start < uint.MaxValue)
        {
            segments[count++] = (range.Start + 1, uint.MaxValue);
        }

        segments[count++] = (0, range.End);
        return count;
    }

    private sealed record ResourceId(string Namespace, string Hub, string ConsumerGroup, string Partition);

    private sealed class CountingViewId(int value) : IComparable<CountingViewId>, IEquatable<CountingViewId>
    {
        public int Comparisons { get; set; }

        public int CompareTo(CountingViewId? other)
        {
            Comparisons++;
            return other is null ? 1 : value.CompareTo(other.Value);
        }

        private int Value => value;

        public bool Equals(CountingViewId? other) => other is not null && value == other.Value;

        public override bool Equals(object? obj) => obj is CountingViewId other && Equals(other);

        public override int GetHashCode() => value.GetHashCode();
    }
}

using System.Collections.Immutable;
using System.Net;
using CsCheck;
using Orleans.Runtime;
using Orleans.Runtime.ClusterServices;
using Orleans.Runtime.GrainDirectory;
using TestExtensions;
using Xunit;

namespace UnitTests.ClusterServices;

[TestArea("Runtime")]
[TestCategory("BVT")]
[TestSuite("BVT")]
[TestProvider("None")]
public sealed class ClusterServiceTopologyTests
{
    private const string AssignmentStrategy = "uniform-hash-ring/v1";

    [Fact]
    public void VisitRangeOwners_ReportsFullRangesOnceAcrossWrappingEndpoints()
    {
        var members = CreateMembers(3).ToImmutableArray();
        var topology = new ClusterServiceTopology(members, 1, (silo, _) => [(uint)(silo.Generation - 1) * 100]);
        var query = RingRange.Create(50, 25);
        var actual = new List<ClusterServicePartitionOwner>();

        topology.VisitRangeOwners(query, static (owner, result) => result.Add(owner), actual);

        Assert.Equal(topology.RangeOwners.ToArray(), actual);
        Assert.Equal(3, actual.Count);
        Assert.Equal(RingRange.Create(0, 100), actual[0].Range);
        Assert.Equal(RingRange.Create(200, 0), actual[^1].Range);
    }

    [Fact]
    public void VisitRangeOwners_HandlesEmptyFullPointExactBoundariesAndCollisions()
    {
        var members = CreateMembers(4).ToImmutableArray();
        var topology = new ClusterServiceTopology(members, 2, (silo, _) => [0, (uint)silo.Generation * 100]);
        var empty = new ClusterServiceTopology([], 1, GetBoundaries);
        var single = new ClusterServiceTopology([members[0]], 1, GetBoundaries);
        RingRange[] queries =
        [
            RingRange.Empty, RingRange.Full, RingRange.FromPoint(0), RingRange.FromPoint(100),
            RingRange.FromPoint(uint.MaxValue), RingRange.Create(100, 200),
            RingRange.Create(uint.MaxValue, 0), RingRange.Create(50, 25), RingRange.Create(400, 100),
        ];

        foreach (var candidate in new[] { empty, single, topology })
        {
            foreach (var query in queries)
            {
                AssertVisitedOwners(candidate, query);
            }
        }

        Assert.Equal(5, topology.RangeOwners.Count);
        Assert.Throws<ArgumentNullException>(() => topology.VisitRangeOwners<int>(RingRange.Empty, null!, 0));
    }

    [Fact]
    public void CsCheck_VisitRangeOwners_MatchesFullScanIncludingExactStarts()
    {
        Gen.UInt.Array[24].Sample(
            values =>
            {
                var members = CreateMembers(8).ToImmutableArray();
                var topology = new ClusterServiceTopology(members, 3, (silo, _) =>
                    values.Skip((silo.Generation - 1) * 3).Take(3).ToArray());
                for (var index = 0; index < values.Length; index++)
                {
                    AssertVisitedOwners(topology, RingRange.Create(values[index], values[(index + 1) % values.Length]));
                    AssertVisitedOwners(topology, RingRange.FromPoint(values[index]));
                    AssertVisitedOwners(topology, RingRange.Create(values[index], unchecked(values[index] - 1)));
                }
            },
            seed: "cluster-service-owner-visitation-v1",
            iter: 120,
            threads: 1);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(1024)]
    [InlineData(16384)]
    public void RingOwnerSearch_UsesLogarithmicProbesAndPointVisitationHasOneResult(int partitions)
    {
        var member = CreateMembers(1)[0];
        var topology = new ClusterServiceTopology([member], partitions, (_, count) =>
            Enumerable.Range(0, count).Select(index => (uint)((ulong)index * uint.MaxValue / (uint)count)).ToArray());
        foreach (var point in new uint[] { 0, 1, 123456789, uint.MaxValue })
        {
            var actual = new List<ClusterServicePartitionOwner>();
            topology.VisitRangeOwners(RingRange.FromPoint(point), static (owner, result) => result.Add(owner), actual, out var probes);
            Assert.InRange(probes, 1, (int)Math.Ceiling(Math.Log2(partitions)) + 2);
            Assert.True(Assert.Single(actual).Range.Contains(point));
        }
    }

    [Fact]
    public void ExplicitAssignments_ProduceTheSameIndexAsProjectedAssignments()
    {
        var members = CreateMembers(4).ToImmutableArray();
        var projected = new ClusterServiceTopology(members, 3, GetBoundaries);
        var assignments = projected.RangeOwners.Select(owner =>
            new ClusterServicePartitionAssignment(owner.SiloAddress, owner.PartitionIndex, owner.Range)).Reverse().ToImmutableArray();
        var explicitTopology = new ClusterServiceTopology(members.Reverse().ToImmutableArray(), 3, assignments);

        Assert.Equal(projected.Members, explicitTopology.Members);
        Assert.Equal(projected.RangeOwners.ToArray(), explicitTopology.RangeOwners.ToArray());
        foreach (var member in members)
        {
            Assert.Equal(projected.GetMemberRangesByPartition(member), explicitTopology.GetMemberRangesByPartition(member));
        }

        AssertVisitedOwners(explicitTopology, RingRange.Create(10, 9));
    }

    [Fact]
    public void ExplicitAssignments_ValidateEligibilityIdentityCoverageAndOverlap()
    {
        var members = CreateMembers(3).ToImmutableArray();
        Assert.Throws<ArgumentException>(() => new ClusterServiceTopology([members[0], members[0]], 1, []));
        Assert.Throws<ArgumentException>(() => new ClusterServiceTopology([members[0]], 1,
            [new(members[1], 0, RingRange.Full)]));
        Assert.Throws<ArgumentException>(() => new ClusterServiceTopology([members[0]], 1,
            [new(members[0], 1, RingRange.Full)]));
        Assert.Throws<ArgumentException>(() => new ClusterServiceTopology([members[0]], 1,
            [new(members[0], 0, RingRange.Create(0, 100)), new(members[0], 0, RingRange.Create(100, 0))]));
        Assert.Throws<ArgumentException>(() => new ClusterServiceTopology(members, 1,
            [new(members[0], 0, RingRange.Create(0, 100)), new(members[1], 0, RingRange.Create(101, 0))]));
        Assert.Throws<ArgumentException>(() => new ClusterServiceTopology(members, 1,
            [new(members[0], 0, RingRange.Create(0, 101)), new(members[1], 0, RingRange.Create(100, 0))]));
        Assert.Throws<ArgumentException>(() => new ClusterServiceTopology(members, 1, []));
    }

    private static void AssertVisitedOwners(ClusterServiceTopology topology, RingRange query)
    {
        var actual = new List<ClusterServicePartitionOwner>();
        topology.VisitRangeOwners(query, static (owner, result) => result.Add(owner), actual);
        var expected = topology.RangeOwners.Where(owner => owner.Range.Intersects(query)).ToHashSet();
        Assert.Equal(expected.Count, actual.Count);
        Assert.True(expected.SetEquals(actual), $"query={query}; expected={string.Join(';', expected)}; actual={string.Join(';', actual)}");
    }

    [Fact]
    public void CsCheck_TopologyProjection_IsDeterministicAndEveryPointHasOneActiveOwner()
    {
        Gen.Int.Array[24].Sample(
            values => VerifyTopologyProjection(values),
            seed: "cluster-service-topology-v1",
            iter: 120,
            threads: 1,
            print: static values => $"values=[{string.Join(',', values)}]");
    }

    private static void VerifyTopologyProjection(int[] values)
    {
        var memberCount = 1 + (int)((uint)values[0] % 6);
        var partitionsPerSilo = 1 + (int)((uint)values[1] % 5);
        var members = CreateMembers(memberCount);
        var order = Enumerable.Range(0, memberCount)
            .OrderBy(index => values[2 + index])
            .ThenBy(static index => index)
            .ToArray();
        var firstSnapshot = CreateSnapshot(members, order);
        var secondSnapshot = CreateSnapshot(members, order.AsEnumerable().Reverse());
        var configuration = CreateConfiguration(partitionsPerSilo: partitionsPerSilo);
        var firstView = new MembershipBasedClusterServiceView(firstSnapshot, configuration, GetBoundaries);
        var secondView = new MembershipBasedClusterServiceView(secondSnapshot, configuration, GetBoundaries);
        var first = firstView.Topology;
        var second = secondView.Topology;

        Assert.Equal(firstView.Id, secondView.Id);
        Assert.Equal(first.Members, second.Members);
        Assert.Equal(first.RangeOwners.ToArray(), second.RangeOwners.ToArray());
        Assert.Equal(memberCount * partitionsPerSilo, first.RangeOwners.Count);

        for (var index = 8; index < values.Length; index++)
        {
            var point = unchecked((uint)values[index]);
            Assert.True(first.TryGetOwner(point, out var owner));
            Assert.True(owner.Range.Contains(point), $"point={point}; owner={owner}");
            Assert.Contains(owner.SiloAddress, first.Members);

            Assert.True(second.TryGetOwner(point, out var repeatedOwner));
            Assert.Equal(owner, repeatedOwner);
        }
    }

    private static ClusterServiceConfiguration CreateConfiguration(
        string serviceId = "test-service",
        int partitionsPerSilo = 1,
        string assignmentStrategy = AssignmentStrategy) =>
        new(serviceId, partitionsPerSilo, assignmentStrategy);

    private static SiloAddress[] CreateMembers(int count) =>
        Enumerable.Range(0, count)
            .Select(index => SiloAddress.New(IPAddress.Loopback, 10_000 + index, generation: index + 1))
            .ToArray();

    private static ClusterMembershipSnapshot CreateSnapshot(
        IReadOnlyList<SiloAddress> members,
        IEnumerable<int> order)
    {
        var builder = ImmutableDictionary.CreateBuilder<SiloAddress, ClusterMember>();
        foreach (var index in order)
        {
            var address = members[index];
            builder.Add(address, new(address, SiloStatus.Active, $"silo-{index}"));
        }

        return new(builder.ToImmutable(), new MembershipVersion(7));
    }

    private static uint[] GetBoundaries(SiloAddress silo, int count) =>
        count == 1
            ? [unchecked((uint)silo.GetConsistentHashCode())]
            : silo.GetUniformHashCodes(count);
}

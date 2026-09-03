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
    public void ConfigurationFingerprint_ChangesWithEveryCompatibilityInput()
    {
        var baseline = CreateConfiguration();

        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(serviceId: "other-service").Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(protocolVersion: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(partitionsPerSilo: 2).Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, CreateConfiguration(assignmentStrategy: "uniform-hash-ring/v2").Fingerprint);
        Assert.Equal(baseline.Fingerprint, CreateConfiguration().Fingerprint);
    }

    [Fact]
    public void ViewId_DirectSuccessorRequiresContiguousMembershipAndMatchingConfiguration()
    {
        var configuration = CreateConfiguration();
        var previous = new ClusterServiceViewId(new(10), configuration.ProtocolVersion, configuration.Fingerprint);

        Assert.True(new ClusterServiceViewId(new(11), configuration.ProtocolVersion, configuration.Fingerprint).IsDirectSuccessorOf(previous));
        Assert.False(new ClusterServiceViewId(new(12), configuration.ProtocolVersion, configuration.Fingerprint).IsDirectSuccessorOf(previous));
        Assert.False(new ClusterServiceViewId(new(11), configuration.ProtocolVersion + 1, configuration.Fingerprint).IsDirectSuccessorOf(previous));
        Assert.False(new ClusterServiceViewId(new(11), configuration.ProtocolVersion, "different").IsDirectSuccessorOf(previous));
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

    [Fact]
    public void DuplicateBoundary_KeepsLastOwnerOfFollowingRange()
    {
        var members = CreateMembers(3);
        var snapshot = CreateSnapshot(members, [0, 1, 2]);
        var configuration = CreateConfiguration();
        var baseline = new ClusterServiceTopology(
            snapshot,
            configuration,
            (silo, _) => [silo == members[0] ? 5u : silo == members[1] ? 10u : 20u]);
        var collision = new ClusterServiceTopology(
            snapshot,
            configuration,
            (silo, _) => [silo == members[2] ? 20u : 10u]);

        Assert.True(baseline.TryGetOwner(15, out var baselineOwner));
        Assert.True(collision.TryGetOwner(15, out var collisionOwner));
        Assert.Equal(members[1], baselineOwner.SiloAddress);
        Assert.Equal(baselineOwner.SiloAddress, collisionOwner.SiloAddress);
        Assert.True(collision.GetRange(members[0], 0).IsEmpty);
        Assert.True(collision.GetRange(members[1], 0).Contains(15));
        Assert.Equal(2, collision.RangeOwners.Count);
    }

    [Fact]
    public void MemberRanges_AreStableUnderConcurrentReads()
    {
        var members = CreateMembers(4);
        var topology = new ClusterServiceTopology(
            CreateSnapshot(members, [0, 1, 2, 3]),
            CreateConfiguration(partitionsPerSilo: 4),
            GetBoundaries);
        var expected = members.Select(topology.GetMemberRanges).ToArray();
        var failures = 0;

        Parallel.For(0, 10_000, index =>
        {
            var memberIndex = index % members.Length;
            if (topology.GetMemberRanges(members[memberIndex]) != expected[memberIndex])
            {
                Interlocked.Increment(ref failures);
            }
        });

        Assert.Equal(0, failures);
        Assert.All(expected, static ranges => Assert.False(ranges.IsDefault));
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
        var first = new ClusterServiceTopology(firstSnapshot, configuration, GetBoundaries);
        var second = new ClusterServiceTopology(secondSnapshot, configuration, GetBoundaries);

        Assert.Equal(first.ViewId, second.ViewId);
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
        int protocolVersion = 1,
        int partitionsPerSilo = 1,
        string assignmentStrategy = AssignmentStrategy) =>
        new(serviceId, protocolVersion, partitionsPerSilo, assignmentStrategy);

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

using System.Collections.Immutable;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService.SiloMetadata;
using Xunit;

namespace UnitTests;

[TestCategory("BVT"), TestCategory("Membership")]
public class ClusterMembershipSnapshotTests
{
    [Fact]
    public void GetSiloStatus_ReturnsDeadForUnknownSiloSeenAtOlderVersion()
    {
        var unknownSilo = CreateSiloAddress(1);
        var knownSilo = CreateSiloAddress(1, port: 11112);
        var snapshot = CreateSnapshot(new ClusterMember(knownSilo, SiloStatus.Active, "known"), version: 2);

        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(unknownSilo, new MembershipVersion(1)));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void GetSiloStatus_ReturnsNoneForUnknownSiloSeenAtCurrentOrNewerVersion(long seenAtVersion)
    {
        var unknownSilo = CreateSiloAddress(1);
        var knownSilo = CreateSiloAddress(1, port: 11112);
        var snapshot = CreateSnapshot(new ClusterMember(knownSilo, SiloStatus.Active, "known"), version: 2);

        Assert.Equal(SiloStatus.None, snapshot.GetSiloStatus(unknownSilo, new MembershipVersion(seenAtVersion)));
    }

    [Fact]
    public void GetSiloStatus_ReturnsDeadForSiloReplacedBySuccessor()
    {
        var silo = CreateSiloAddress(1);
        var successor = CreateSiloAddress(2);
        var snapshot = CreateSnapshot(new ClusterMember(successor, SiloStatus.Active, "silo"), version: 2);

        Assert.Equal(SiloStatus.Dead, snapshot.GetSiloStatus(silo, new MembershipVersion(2)));
    }

    [Fact]
    public void ClusterMember_DistinguishesUnavailableFromAvailableEmptyMetadata()
    {
        var silo = CreateSiloAddress(1);

        var unavailable = new ClusterMember(silo, SiloStatus.Active, "silo");
        var availableEmpty = new ClusterMember(silo, SiloStatus.Active, "silo", SiloMetadata.Empty);

        Assert.False(unavailable.IsMetadataAvailable);
        Assert.True(availableEmpty.IsMetadataAvailable);
        Assert.Null(unavailable.Metadata);
        Assert.Empty(availableEmpty.Metadata.Metadata);
        Assert.NotEqual(unavailable, availableEmpty);
    }

    [Fact]
    public void CreateUpdate_IncludesMetadataOnlyChanges()
    {
        var silo = CreateSiloAddress(1);
        var previous = CreateSnapshot(
            new ClusterMember(silo, SiloStatus.Active, "silo", new SiloMetadata([new KeyValuePair<string, string>("region", "west")])),
            version: 1);
        var current = CreateSnapshot(
            new ClusterMember(silo, SiloStatus.Active, "silo", new SiloMetadata([new KeyValuePair<string, string>("region", "east")])),
            version: 2);

        var update = current.CreateUpdate(previous);

        Assert.True(update.HasChanges);
        var change = Assert.Single(update.Changes);
        Assert.Equal("east", change.Metadata.Metadata["region"]);
    }

    private static ClusterMembershipSnapshot CreateSnapshot(ClusterMember member, long version)
        => new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member.SiloAddress, member), new MembershipVersion(version));

    private static SiloAddress CreateSiloAddress(int generation, int port = 11111)
        => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);
}

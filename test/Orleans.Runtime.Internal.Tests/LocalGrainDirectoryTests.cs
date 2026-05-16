using System.Collections.Immutable;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace UnitTests;

[TestCategory("BVT"), TestCategory("GrainDirectory")]
public class LocalGrainDirectoryTests
{
    [Theory]
    [InlineData(SiloStatus.Active)]
    [InlineData(SiloStatus.ShuttingDown)]
    [InlineData(SiloStatus.Stopping)]
    public void IsDefunctActivation_DoesNotRemoveNonDeadSilos(SiloStatus status)
    {
        var silo = CreateSiloAddress(1);
        var address = CreateGrainAddress(silo);
        var snapshot = CreateSnapshot(new ClusterMember(silo, status, "silo"), version: 2);

        Assert.False(LocalGrainDirectory.IsDefunctActivation(address, snapshot));
    }

    [Fact]
    public void IsDefunctActivation_RemovesDeadSilos()
    {
        var silo = CreateSiloAddress(1);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(silo, SiloStatus.Dead, "silo"), version: 2);

        Assert.True(LocalGrainDirectory.IsDefunctActivation(address, snapshot));
    }

    [Fact]
    public void IsDefunctActivation_DoesNotRemoveUnknownSiloWithoutNewerMembershipVersion()
    {
        var silo = CreateSiloAddress(1);
        var unrelatedSilo = CreateSiloAddress(1, port: 11112);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(unrelatedSilo, SiloStatus.Active, "other"), version: 2);

        Assert.False(LocalGrainDirectory.IsDefunctActivation(address, snapshot));
    }

    [Fact]
    public void IsDefunctActivation_RemovesUnknownSiloWithOlderMembershipVersion()
    {
        var silo = CreateSiloAddress(1);
        var unrelatedSilo = CreateSiloAddress(1, port: 11112);
        var address = CreateGrainAddress(silo, membershipVersion: 1);
        var snapshot = CreateSnapshot(new ClusterMember(unrelatedSilo, SiloStatus.Active, "other"), version: 2);

        Assert.True(LocalGrainDirectory.IsDefunctActivation(address, snapshot));
    }

    [Fact]
    public void IsDefunctActivation_RemovesSiloReplacedBySuccessor()
    {
        var silo = CreateSiloAddress(1);
        var successor = CreateSiloAddress(2);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(successor, SiloStatus.Active, "silo"), version: 2);

        Assert.True(LocalGrainDirectory.IsDefunctActivation(address, snapshot));
    }

    private static ClusterMembershipSnapshot CreateSnapshot(ClusterMember member, long version)
        => new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member.SiloAddress, member), new MembershipVersion(version));

    private static GrainAddress CreateGrainAddress(SiloAddress siloAddress, long membershipVersion = 1)
        => new()
        {
            GrainId = GrainId.Create("test-grain", Guid.NewGuid().ToString("N")),
            ActivationId = ActivationId.NewId(),
            SiloAddress = siloAddress,
            MembershipVersion = new MembershipVersion(membershipVersion)
        };

    private static SiloAddress CreateSiloAddress(int generation, int port = 11111)
        => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), generation);
}

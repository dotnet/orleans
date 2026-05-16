using System.Collections.Immutable;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.GrainDirectory;
using Xunit;

namespace UnitTests;

[TestCategory("BVT"), TestCategory("GrainDirectory")]
public class GrainDirectoryHandoffManagerTests
{
    [Theory]
    [InlineData(SiloStatus.Active, true)]
    [InlineData(SiloStatus.ShuttingDown, true)]
    [InlineData(SiloStatus.Stopping, true)]
    [InlineData(SiloStatus.Dead, false)]
    public void IsTransferableRegistration_UsesSnapshotStatus(SiloStatus status, bool expected)
    {
        var silo = CreateSiloAddress(1);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(silo, status, "silo"), version: 2);

        Assert.Equal(expected, GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    [Fact]
    public void IsTransferableRegistration_AllowsUnknownSiloWithoutNewerMembershipVersion()
    {
        var silo = CreateSiloAddress(1);
        var unrelatedSilo = CreateSiloAddress(1, port: 11112);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(unrelatedSilo, SiloStatus.Active, "other"), version: 2);

        Assert.True(GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    [Fact]
    public void IsTransferableRegistration_RejectsUnknownSiloWithOlderMembershipVersion()
    {
        var silo = CreateSiloAddress(1);
        var unrelatedSilo = CreateSiloAddress(1, port: 11112);
        var address = CreateGrainAddress(silo, membershipVersion: 1);
        var snapshot = CreateSnapshot(new ClusterMember(unrelatedSilo, SiloStatus.Active, "other"), version: 2);

        Assert.False(GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    [Fact]
    public void IsTransferableRegistration_RejectsSiloReplacedBySuccessor()
    {
        var silo = CreateSiloAddress(1);
        var successor = CreateSiloAddress(2);
        var address = CreateGrainAddress(silo, membershipVersion: 2);
        var snapshot = CreateSnapshot(new ClusterMember(successor, SiloStatus.Active, "silo"), version: 2);

        Assert.False(GrainDirectoryHandoffManager.IsTransferableRegistration(address, snapshot));
    }

    private static ClusterMembershipSnapshot CreateSnapshot(ClusterMember member, long version)
        => new(ImmutableDictionary<SiloAddress, ClusterMember>.Empty.Add(member.SiloAddress, member), new MembershipVersion(version));

    private static GrainAddress CreateGrainAddress(SiloAddress siloAddress, long membershipVersion)
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

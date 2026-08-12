using System.Collections.Immutable;
using System.Net;
using Orleans.Runtime;
using Orleans.Runtime.MembershipService;
using Xunit;

namespace UnitTests;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("Membership")]
[TestCategory("BVT"), TestCategory("Membership")]
public class UnknownSiloStatusCacheTests
{
    [Fact]
    public void UnknownSiloRequiresCausallyNewerSnapshotsBeforeBeingDeclaredDead()
    {
        var cache = new UnknownSiloStatusCache();
        var silo = CreateSiloAddress();

        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(CreateSnapshot(1), silo));
        cache.Observe(CreateSnapshot(10));
        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(CreateSnapshot(10), silo));
        cache.Observe(CreateSnapshot(11));
        Assert.Equal(SiloStatus.Dead, cache.GetSiloStatus(CreateSnapshot(11), silo));
    }

    [Fact]
    public void KnownSiloClearsUnknownClassification()
    {
        var cache = new UnknownSiloStatusCache();
        var silo = CreateSiloAddress();

        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(CreateSnapshot(1), silo));
        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(CreateSnapshot(2), silo));
        Assert.Equal(SiloStatus.Active, cache.GetSiloStatus(CreateSnapshot(3, new ClusterMember(silo, SiloStatus.Active, "silo")), silo));
        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(CreateSnapshot(4), silo));
        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(CreateSnapshot(5), silo));
        Assert.Equal(SiloStatus.Dead, cache.GetSiloStatus(CreateSnapshot(6), silo));
    }

    [Fact]
    public void KnownDeadSiloIsReturnedImmediately()
    {
        var cache = new UnknownSiloStatusCache();
        var silo = CreateSiloAddress();
        var snapshot = CreateSnapshot(1, new ClusterMember(silo, SiloStatus.Dead, "silo"));

        Assert.Equal(SiloStatus.Dead, cache.GetSiloStatus(snapshot, silo));
    }

    private static ClusterMembershipSnapshot CreateSnapshot(long version, params ClusterMember[] members) =>
        new(members.ToImmutableDictionary(member => member.SiloAddress), new MembershipVersion(version));

    private static SiloAddress CreateSiloAddress() =>
        SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 11111), 1);
}

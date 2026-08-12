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
    public void UnknownSiloRequiresFullRefreshStartedAfterObservation()
    {
        var cache = new UnknownSiloStatusCache();
        var silo = CreateSiloAddress();
        var inFlightRefresh = cache.OnFullRefreshStarted();
        var snapshot = CreateSnapshot(1);

        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(snapshot, silo));
        cache.OnFullRefreshCompleted(inFlightRefresh, snapshot);
        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(snapshot, silo));

        var causalRefresh = cache.OnFullRefreshStarted();
        cache.OnFullRefreshCompleted(causalRefresh, snapshot);

        Assert.Equal(SiloStatus.Dead, cache.GetSiloStatus(snapshot, silo));
    }

    [Fact]
    public void KnownSiloClearsUnknownClassification()
    {
        var cache = new UnknownSiloStatusCache();
        var silo = CreateSiloAddress();
        var unknownSnapshot = CreateSnapshot(1);

        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(unknownSnapshot, silo));
        var refresh = cache.OnFullRefreshStarted();
        var activeSnapshot = CreateSnapshot(1, new ClusterMember(silo, SiloStatus.Active, "silo"));
        cache.OnFullRefreshCompleted(refresh, activeSnapshot);
        Assert.Equal(SiloStatus.Active, cache.GetSiloStatus(activeSnapshot, silo));

        Assert.Equal(SiloStatus.None, cache.GetSiloStatus(unknownSnapshot, silo));
        refresh = cache.OnFullRefreshStarted();
        cache.OnFullRefreshCompleted(refresh, unknownSnapshot);
        Assert.Equal(SiloStatus.Dead, cache.GetSiloStatus(unknownSnapshot, silo));
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

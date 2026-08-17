using System.Collections.Immutable;
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task UnknownSilosShareSourceRefresh()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1));
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var firstSilo = CreateSiloAddress();
        var secondSilo = CreateSiloAddress(port: 11112);
        var snapshot = CreateSnapshot(1);

        var statuses = await cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(firstSilo, secondSilo),
            CancellationToken.None);

        Assert.Equal(SiloStatus.Dead, statuses[firstSilo]);
        Assert.Equal(SiloStatus.Dead, statuses[secondSilo]);
        Assert.Equal(1, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task KnownSiloClearsUnknownClassification()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1));
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var silo = CreateSiloAddress();
        var unknownSnapshot = CreateSnapshot(1);

        Assert.Equal(
            SiloStatus.Dead,
            (await cache.GetSiloStatuses(unknownSnapshot, SiloAddresses(silo), CancellationToken.None))[silo]);
        var activeSnapshot = CreateSnapshot(1, new ClusterMember(silo, SiloStatus.Active, "silo"));
        Assert.Equal(
            SiloStatus.Active,
            (await cache.GetSiloStatuses(activeSnapshot, SiloAddresses(silo), CancellationToken.None))[silo]);
        Assert.Equal(
            SiloStatus.Dead,
            (await cache.GetSiloStatuses(unknownSnapshot, SiloAddresses(silo), CancellationToken.None))[silo]);
        Assert.Equal(2, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task KnownDeadSiloIsReturnedImmediately()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1));
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var silo = CreateSiloAddress();
        var snapshot = CreateSnapshot(1, new ClusterMember(silo, SiloStatus.Dead, "silo"));

        Assert.Equal(
            SiloStatus.Dead,
            (await cache.GetSiloStatuses(snapshot, SiloAddresses(silo), CancellationToken.None))[silo]);
        Assert.Equal(0, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task CancellationIsPropagatedToSourceRefresh()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1));
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var cancellation = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cache.GetSiloStatuses(
                CreateSnapshot(1),
                SiloAddresses(CreateSiloAddress()),
                cancellation).AsTask());
        Assert.Equal(cancellation, membershipManager.LastRefreshCancellationToken);
    }

    private static ClusterMembershipSnapshot CreateSnapshot(long version, params ClusterMember[] members) =>
        new(members.ToImmutableDictionary(member => member.SiloAddress), new MembershipVersion(version));

    private static MembershipTableSnapshot CreateMembershipTableSnapshot(long version) =>
        new(new MembershipVersion(version), ImmutableDictionary<SiloAddress, MembershipEntry>.Empty);

    private static SiloAddress CreateSiloAddress(int port = 11111) =>
        SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), 1);

    private static HashSet<SiloAddress> SiloAddresses(params SiloAddress[] addresses) => [.. addresses];

    private sealed class TestMembershipManager(MembershipTableSnapshot snapshot) : IMembershipManager
    {
        public int SourceRefreshCount { get; private set; }

        public CancellationToken LastRefreshCancellationToken { get; private set; }

        public MembershipTableSnapshot CurrentSnapshot { get; } = snapshot;

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => GetMembershipUpdates();

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public Task Refresh(
            MembershipVersion? targetVersion,
            CancellationToken cancellationToken,
            bool requireFresh = false)
        {
            LastRefreshCancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            if (requireFresh)
            {
                SourceRefreshCount++;
            }

            return Task.CompletedTask;
        }

        public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TrySuspectSilo(
            SiloAddress silo,
            SiloAddress? indirectProbingSilo,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task ProcessGossipSnapshot(MembershipTableSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateIAmAlive(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Participate(ISiloLifecycle lifecycle)
        {
        }

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        private static async IAsyncEnumerable<MembershipTableSnapshot> GetMembershipUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using CsCheck;
using Orleans.Configuration;
using Orleans.Runtime;
using Orleans.Runtime.Dissemination;
using Xunit;

namespace UnitTests.Dissemination;

[TestCategory("BVT"), TestCategory("Dissemination")]
public class DisseminationMembershipSnapshotTests
{
    [Fact]
    public void CollectionsSatisfyMembershipAndRoutingInvariants()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 127], Gen.Int[0, 1], Gen.Int[0, 144], static (count, localSeed, localMode, fanoutSeed) =>
        {
            return new ValidSnapshotTestCase(
                count,
                localSeed,
                LocalIsMember: localMode == 0,
                RawFanout: fanoutSeed - 16);
        }).Sample(testCase =>
        {
            var members = CreateSilos(testCase.Count).ToImmutableArray();
            var local = testCase.LocalIsMember
                ? members[testCase.LocalSeed % members.Length]
                : CreateSilo(20000 + testCase.LocalSeed);
            var snapshot = CreateSnapshot(local, members, testCase.RawFanout);

            Assert.Equal(members, snapshot.Members);
            AssertNoDuplicates(nameof(snapshot.Members), snapshot.Members);

            foreach (var member in members)
            {
                Assert.True(snapshot.ContainsMember(member));
            }

            var outsideMember = CreateSilo(25000 + testCase.LocalSeed);
            Assert.False(snapshot.ContainsMember(outsideMember));

            var originatorTargets = snapshot.OriginatorTreeTargets;
            var forwardingTargets = snapshot.ForwardingTreeTargets;

            AssertNoDuplicates(nameof(originatorTargets), originatorTargets);
            AssertNoDuplicates(nameof(forwardingTargets), forwardingTargets);
            AssertSubset(nameof(originatorTargets), originatorTargets, members);
            AssertSubset(nameof(forwardingTargets), forwardingTargets, members);
            Assert.DoesNotContain(local, originatorTargets);
            Assert.DoesNotContain(local, forwardingTargets);
            Assert.Equal(originatorTargets, snapshot.OriginatorTreeTargets);
            Assert.Equal(forwardingTargets, snapshot.ForwardingTreeTargets);

            if (!members.Contains(local))
            {
                Assert.Empty(originatorTargets);
                Assert.Empty(forwardingTargets);
                return;
            }

            var effectiveFanout = GetEffectiveFanout(members.Length, testCase.RawFanout);
            var maxTargets = Math.Max(0, members.Length - 1);
            Assert.True(
                originatorTargets.Length <= Math.Min(effectiveFanout * 2, maxTargets),
                "Originator target count exceeded the expected bound.");
            Assert.True(
                forwardingTargets.Length <= Math.Min(effectiveFanout, maxTargets),
                "Forwarding target count exceeded the expected bound.");
        });
    }

    [Fact]
    public void AntiEntropyPeerSelectionSatisfiesMembershipInvariants()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 127], Gen.Int[0, 1], Gen.Int[0, 64], static (count, localSeed, localMode, requestedCount) =>
        {
            return new AntiEntropyTestCase(
                count,
                localSeed,
                LocalIsMember: localMode == 0,
                RequestedCount: requestedCount);
        }).Sample(testCase =>
        {
            var members = CreateSilos(testCase.Count).ToImmutableArray();
            var local = testCase.LocalIsMember
                ? members[testCase.LocalSeed % members.Length]
                : CreateSilo(20000 + testCase.LocalSeed);
            var snapshot = CreateSnapshot(local, members, fanout: 4);

            var selectedPeers = snapshot.SelectAntiEntropyPeers(testCase.RequestedCount);

            AssertNoDuplicates("Anti-entropy peers", selectedPeers);
            AssertSubset("Anti-entropy peers", selectedPeers, members);
            Assert.DoesNotContain(local, selectedPeers);

            var expectedCount = members.Contains(local)
                ? Math.Min(testCase.RequestedCount, Math.Max(0, members.Length - 1))
                : 0;
            Assert.Equal(expectedCount, selectedPeers.Length);
        });
    }

    [Fact]
    public void ConstructorRejectsDuplicateMembers()
    {
        Gen.Select(Gen.Int[1, 64], Gen.Int[0, 63], static (count, duplicateSeed) => (Count: count, DuplicateSeed: duplicateSeed))
            .Sample(testCase =>
            {
                var members = CreateSilos(testCase.Count);
                var duplicate = members[testCase.DuplicateSeed % members.Length];
                var invalidMembers = members.Append(duplicate).ToImmutableArray();

                var exception = Assert.Throws<ArgumentException>(() => CreateSnapshot(
                    members[0],
                    invalidMembers,
                    fanout: 4));
                Assert.Equal("members", exception.ParamName);
            });
    }

    private static DisseminationMembershipSnapshot CreateSnapshot(
        SiloAddress localSilo,
        ImmutableArray<SiloAddress> members,
        int fanout) => new(
            new MembershipVersion(1),
            localSilo,
            members,
            CreateOverlayOptions(fanout));

    private static DisseminationOverlayOptions CreateOverlayOptions(int fanout) => new()
    {
        FanOutFactor = _ => fanout,
    };

    private static int GetEffectiveFanout(int memberCount, int rawFanout) =>
        memberCount <= 1 ? 1 : Math.Clamp(rawFanout, 1, memberCount);

    private static void AssertNoDuplicates(string name, IEnumerable<SiloAddress> values)
    {
        var array = values.ToArray();
        Assert.True(array.Length == array.Distinct().Count(), $"{name} contains duplicates.");
    }

    private static void AssertSubset(string name, IEnumerable<SiloAddress> values, IEnumerable<SiloAddress> expectedValues)
    {
        var expected = expectedValues.ToHashSet();
        foreach (var value in values)
        {
            Assert.True(expected.Contains(value), $"{name} contains {value} outside the expected membership set.");
        }
    }

    private static SiloAddress CreateSilo(int port) => SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), port);

    private static SiloAddress[] CreateSilos(int count) =>
        Enumerable.Range(11111, count).Select(CreateSilo).OrderBy(static silo => silo).ToArray();

    private readonly record struct ValidSnapshotTestCase(
        int Count,
        int LocalSeed,
        bool LocalIsMember,
        int RawFanout);

    private readonly record struct AntiEntropyTestCase(
        int Count,
        int LocalSeed,
        bool LocalIsMember,
        int RequestedCount);

    [Fact]
    public void ActiveScopeProjectionIncludesOnlyActiveSilos()
    {
        var (snapshots, manager, silos) = CreateScopeProjections();

        Assert.Equal(new[] { silos.Local, silos.Active, silos.ActiveTwo }, snapshots.ActiveMembers.Members);
        Assert.Equal(new MembershipVersion(42), snapshots.ActiveMembers.MembershipVersion);
        Assert.DoesNotContain(silos.Joining, snapshots.ActiveMembers.Members);
        Assert.DoesNotContain(silos.ShuttingDown, snapshots.ActiveMembers.Members);
        Assert.DoesNotContain(silos.Stopping, snapshots.ActiveMembers.Members);
        Assert.DoesNotContain(silos.Dead, snapshots.ActiveMembers.Members);
        Assert.Equal(1, manager.SnapshotReadCount);
    }

    [Fact]
    public void AllEligibleScopeProjectionIncludesJoiningActiveShuttingDownAndStoppingSilos()
    {
        var (snapshots, manager, silos) = CreateScopeProjections();

        Assert.Equal(
            new[] { silos.Local, silos.Active, silos.ActiveTwo, silos.Joining, silos.ShuttingDown, silos.Stopping },
            snapshots.AllMembers.Members);
        Assert.Equal(new MembershipVersion(42), snapshots.AllMembers.MembershipVersion);
        Assert.DoesNotContain(silos.Dead, snapshots.AllMembers.Members);
        Assert.Equal(1, manager.SnapshotReadCount);
    }

    [Fact]
    public void ScopeProjectionsShareTheSameSourceMembershipVersion()
    {
        var (snapshots, manager, _) = CreateScopeProjections();

        Assert.Equal(new MembershipVersion(42), snapshots.MembershipVersion);
        Assert.Equal(snapshots.MembershipVersion, snapshots.ActiveMembers.MembershipVersion);
        Assert.Equal(snapshots.MembershipVersion, snapshots.AllMembers.MembershipVersion);
        Assert.Equal(1, manager.SnapshotReadCount);
        Assert.NotSame(snapshots.ActiveMembers, snapshots.AllMembers);
    }

    [Fact]
    public void ScopeProjectionTreeIsDeterministicForSelectedMemberArray()
    {
        var first = CreateScopeProjections(reverseSourceEntries: false).Snapshots;
        var second = CreateScopeProjections(reverseSourceEntries: true).Snapshots;
        var active = first.ActiveMembers.Members;
        var all = first.AllMembers.Members;

        Assert.Equal(active, second.ActiveMembers.Members);
        Assert.Equal(all, second.AllMembers.Members);
        Assert.Equal(new[] { active[1], active[2] }, first.ActiveMembers.OriginatorTreeTargets);
        Assert.Equal(new[] { active[2] }, first.ActiveMembers.ForwardingTreeTargets);
        Assert.Equal(
            new[] { all[1], all[2], all[3] },
            first.AllMembers.OriginatorTreeTargets);
        Assert.Equal(new[] { all[2], all[3] }, first.AllMembers.ForwardingTreeTargets);
        Assert.Equal(first.ActiveMembers.OriginatorTreeTargets, second.ActiveMembers.OriginatorTreeTargets);
        Assert.Equal(first.AllMembers.ForwardingTreeTargets, second.AllMembers.ForwardingTreeTargets);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopeProjectionOrderingDoesNotDependOnStartTimePrecision(bool activeMembers)
    {
        var members = CreateSilos(6).Select(static silo => SiloAddress.New(silo.Endpoint, 42)).ToArray();
        var entries = members.Select((member, index) =>
        {
            var entry = CreateScopeMembershipEntry(member, activeMembers ? SiloStatus.Active : SiloStatus.Joining, 0);
            // A joining silo can publish its own entry before reading back the storage-rounded timestamp.
            entry.StartTime = DateTime.UnixEpoch.AddTicks(index == 0 ? 1 : 0);
            return entry;
        }).ToArray();
        var persistedEntries = entries.Select(entry =>
        {
            var persisted = entry.Copy();
            persisted.StartTime = LogFormatter.ParseDate(LogFormatter.PrintDate(entry.StartTime));
            return persisted;
        }).ToArray();

        foreach (var local in members)
        {
            var original = CreateProjections(entries, local);
            var persisted = CreateProjections(persistedEntries, local);

            Assert.Equal(activeMembers ? members : [], original.ActiveMembers.Members);
            Assert.Equal(activeMembers ? members : [], persisted.ActiveMembers.Members);
            Assert.Equal(members, original.AllMembers.Members);
            Assert.Equal(members, persisted.AllMembers.Members);
            Assert.Equal(original.ActiveMembers.OriginatorTreeTargets, persisted.ActiveMembers.OriginatorTreeTargets);
            Assert.Equal(original.ActiveMembers.ForwardingTreeTargets, persisted.ActiveMembers.ForwardingTreeTargets);
            Assert.Equal(original.AllMembers.OriginatorTreeTargets, persisted.AllMembers.OriginatorTreeTargets);
            Assert.Equal(original.AllMembers.ForwardingTreeTargets, persisted.AllMembers.ForwardingTreeTargets);
        }

        static DisseminationMembershipSnapshots CreateProjections(MembershipEntry[] entries, SiloAddress local) =>
            new DisseminationMembership(
                new CountingMembershipManager(new(
                    new MembershipVersion(42),
                    entries.ToImmutableDictionary(static entry => entry.SiloAddress))),
                new ScopeLocalSiloDetails(local),
                Microsoft.Extensions.Options.Options.Create(new DisseminationOptions
                {
                    Overlay = CreateOverlayOptions(2),
                })).CurrentSnapshots;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MembershipVersionChangesPreserveAntiEntropyPeerRotation(bool activeScope)
    {
        var members = CreateSilos(5);
        var local = members[0];
        var peers = members[1..];
        var manager = new MutableMembershipManager(
            CreateSourceSnapshot(1, local, peers),
            TestContext.Current.CancellationToken);
        var membership = new DisseminationMembership(
            manager,
            new ScopeLocalSiloDetails(local),
            Microsoft.Extensions.Options.Options.Create(new DisseminationOptions()));
        var scope = activeScope ? DisseminationMembershipScope.ActiveMembers : DisseminationMembershipScope.AllMembers;

        for (var round = 0; round < peers.Length * 2; round++)
        {
            manager.SetSnapshot(CreateSourceSnapshot(round + 1, local, peers));
            var snapshot = membership.GetSnapshot(scope);

            Assert.Equal(new MembershipVersion(round + 1), snapshot.MembershipVersion);
            Assert.Equal(new[] { peers[round % peers.Length] }, snapshot.SelectAntiEntropyPeers(1));
        }
    }

    [Fact]
    public void AntiEntropyPeerRotationWrapsWhenMembershipShrinks()
    {
        var members = CreateSilos(5);
        var local = members[0];
        var peers = members[1..];
        var manager = new MutableMembershipManager(
            CreateSourceSnapshot(1, local, peers),
            TestContext.Current.CancellationToken);
        var membership = new DisseminationMembership(
            manager,
            new ScopeLocalSiloDetails(local),
            Microsoft.Extensions.Options.Options.Create(new DisseminationOptions()));
        var previous = membership.CurrentSnapshot;
        Assert.Equal(peers[..3], previous.SelectAntiEntropyPeers(3));

        manager.SetSnapshot(CreateSourceSnapshot(2, local, peers[..2]));
        var current = membership.CurrentSnapshot;

        Assert.Equal(new[] { peers[1], peers[0] }, current.SelectAntiEntropyPeers(2));
        Assert.Equal(new[] { peers[1] }, current.SelectAntiEntropyPeers(1));
    }

    [Fact]
    public void ScopeProjectionAntiEntropySelectionIsDeterministic()
    {
        var snapshots = CreateScopeProjections().Snapshots;
        var activePeers = snapshots.ActiveMembers.Members[1..];
        var allPeers = snapshots.AllMembers.Members[1..];

        Assert.Equal(new[] { activePeers[0] }, snapshots.ActiveMembers.SelectAntiEntropyPeers(1));
        Assert.Equal(new[] { activePeers[1] }, snapshots.ActiveMembers.SelectAntiEntropyPeers(1));
        Assert.Equal(new[] { activePeers[0] }, snapshots.ActiveMembers.SelectAntiEntropyPeers(1));

        for (var i = 0; i < allPeers.Length; i++)
        {
            Assert.Equal(new[] { allPeers[i] }, snapshots.AllMembers.SelectAntiEntropyPeers(1));
        }

        Assert.Equal(new[] { allPeers[0] }, snapshots.AllMembers.SelectAntiEntropyPeers(1));
        Assert.Equal(new MembershipVersion(42), snapshots.ActiveMembers.MembershipVersion);
        Assert.Equal(new MembershipVersion(42), snapshots.AllMembers.MembershipVersion);
    }

    [Fact]
    public async Task ConcurrentRefreshDoesNotReturnOlderProjection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(32001);
        var firstPeer = CreateSilo(32002);
        var secondPeer = CreateSilo(32003);
        var manager = new MutableMembershipManager(CreateSourceSnapshot(40, local), cancellationToken);
        var options = new BlockingDisseminationOptions(cancellationToken);
        var membership = new DisseminationMembership(
            manager,
            new ScopeLocalSiloDetails(local),
            options);
        Assert.Equal(new MembershipVersion(40), membership.CurrentSnapshots.MembershipVersion);

        manager.SetSnapshot(CreateSourceSnapshot(41, local, firstPeer));
        var firstRefresh = Task.Run(() => membership.CurrentSnapshots, cancellationToken);
        await options.RefreshBlocked.Task.WaitAsync(cancellationToken);

        manager.SetSnapshot(CreateSourceSnapshot(42, local, firstPeer, secondPeer));
        var secondRefresh = Task.Run(() => membership.CurrentSnapshots, cancellationToken);
        await manager.ThirdRead.Task.WaitAsync(cancellationToken);
        options.ReleaseRefresh();

        Assert.Equal(new MembershipVersion(41), (await firstRefresh).MembershipVersion);
        var latest = await secondRefresh;
        Assert.Equal(new MembershipVersion(42), latest.MembershipVersion);
        Assert.Equal(new[] { local, firstPeer, secondPeer }, latest.ActiveMembers.Members);
        Assert.Equal(3, manager.SnapshotReadCount);
    }

    [Fact]
    public async Task StaleConcurrentRefreshDoesNotRegressNewerProjection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var local = CreateSilo(32101);
        var firstPeer = CreateSilo(32102);
        var secondPeer = CreateSilo(32103);
        var manager = new MutableMembershipManager(CreateSourceSnapshot(40, local), cancellationToken)
        {
            BlockSecondSnapshotRead = true,
        };
        var membership = new DisseminationMembership(
            manager,
            new ScopeLocalSiloDetails(local),
            Microsoft.Extensions.Options.Options.Create(new DisseminationOptions()));
        Assert.Equal(new MembershipVersion(40), membership.CurrentSnapshots.MembershipVersion);

        manager.SetSnapshot(CreateSourceSnapshot(41, local, firstPeer));
        var staleRefresh = Task.Run(() => membership.CurrentSnapshots, cancellationToken);
        await manager.SecondReadCaptured.Task.WaitAsync(cancellationToken);

        manager.SetSnapshot(CreateSourceSnapshot(42, local, firstPeer, secondPeer));
        var newer = await Task.Run(() => membership.CurrentSnapshots, cancellationToken);
        manager.ReleaseSecondRead();
        var stale = await staleRefresh;

        Assert.Equal(new MembershipVersion(42), newer.MembershipVersion);
        Assert.Equal(new MembershipVersion(42), stale.MembershipVersion);
        Assert.Equal(new[] { local, firstPeer, secondPeer }, membership.CurrentSnapshots.ActiveMembers.Members);
        Assert.Equal(4, manager.SnapshotReadCount);
    }

    private static (
        DisseminationMembershipSnapshots Snapshots,
        CountingMembershipManager Manager,
        ScopeSilos Silos) CreateScopeProjections(bool reverseSourceEntries = false)
    {
        var silos = new ScopeSilos(
            CreateSilo(31001),
            CreateSilo(31002),
            CreateSilo(31003),
            CreateSilo(31004),
            CreateSilo(31005),
            CreateSilo(31006),
            CreateSilo(31007));
        var entries = new[]
        {
            CreateScopeMembershipEntry(silos.Dead, SiloStatus.Dead, 5),
            CreateScopeMembershipEntry(silos.Stopping, SiloStatus.Stopping, 4),
            CreateScopeMembershipEntry(silos.ShuttingDown, SiloStatus.ShuttingDown, 3),
            CreateScopeMembershipEntry(silos.Joining, SiloStatus.Joining, 2),
            CreateScopeMembershipEntry(silos.ActiveTwo, SiloStatus.Active, 2),
            CreateScopeMembershipEntry(silos.Active, SiloStatus.Active, 1),
            CreateScopeMembershipEntry(silos.Local, SiloStatus.Active, 0),
        };
        if (reverseSourceEntries)
        {
            Array.Reverse(entries);
        }

        var source = new MembershipTableSnapshot(
            new MembershipVersion(42),
            entries.ToImmutableDictionary(static entry => entry.SiloAddress));
        var manager = new CountingMembershipManager(source);
        var membership = new DisseminationMembership(
            manager,
            new ScopeLocalSiloDetails(silos.Local),
            Microsoft.Extensions.Options.Options.Create(new DisseminationOptions
            {
                Overlay = new DisseminationOverlayOptions
                {
                    FanOutFactor = static _ => 2,
                },
            }));

        return (membership.CurrentSnapshots, manager, silos);
    }

    private static MembershipEntry CreateScopeMembershipEntry(
        SiloAddress silo,
        SiloStatus status,
        int startSeconds) => new()
        {
            SiloAddress = silo,
            Status = status,
            ProxyPort = silo.Endpoint.Port,
            HostName = "localhost",
            SiloName = silo.ToParsableString(),
            RoleName = "test",
            StartTime = DateTime.UnixEpoch.AddSeconds(startSeconds),
            IAmAliveTime = DateTime.UnixEpoch.AddSeconds(startSeconds),
        };

    private static MembershipTableSnapshot CreateSourceSnapshot(
        long version,
        SiloAddress local,
        params SiloAddress[] peers)
    {
        var entries = peers
            .Prepend(local)
            .Select((silo, index) => CreateScopeMembershipEntry(silo, SiloStatus.Active, index))
            .ToImmutableDictionary(static entry => entry.SiloAddress);
        return new(new MembershipVersion(version), entries);
    }

    private readonly record struct ScopeSilos(
        SiloAddress Local,
        SiloAddress Active,
        SiloAddress ActiveTwo,
        SiloAddress Joining,
        SiloAddress ShuttingDown,
        SiloAddress Stopping,
        SiloAddress Dead);

    private sealed class ScopeLocalSiloDetails(SiloAddress siloAddress) : ILocalSiloDetails
    {
        public string Name => "test";

        public string ClusterId => "test";

        public string DnsHostName => "localhost";

        public SiloAddress SiloAddress => siloAddress;

        public SiloAddress GatewayAddress => siloAddress;
    }

    private sealed class CountingMembershipManager(
        MembershipTableSnapshot snapshot)
        : Orleans.Runtime.MembershipService.IMembershipManager
    {
        public int SnapshotReadCount { get; private set; }

        public MembershipTableSnapshot CurrentSnapshot
        {
            get
            {
                SnapshotReadCount++;
                return snapshot;
            }
        }

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates =>
            EmptyUpdates();

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TrySuspectSilo(
            SiloAddress silo,
            SiloAddress? indirectProbingSilo,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ProcessGossipSnapshot(
            MembershipTableSnapshot value,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateIAmAlive(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
        }

        private static async IAsyncEnumerable<MembershipTableSnapshot> EmptyUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class MutableMembershipManager(
        MembershipTableSnapshot snapshot,
        CancellationToken cancellationToken)
        : Orleans.Runtime.MembershipService.IMembershipManager
    {
        private readonly ManualResetEventSlim _releaseSecondRead = new();
        private MembershipTableSnapshot _snapshot = snapshot;
        private int _snapshotReadCount;

        public int SnapshotReadCount => Volatile.Read(ref _snapshotReadCount);

        public bool BlockSecondSnapshotRead { get; init; }

        public TaskCompletionSource SecondReadCaptured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ThirdRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public MembershipTableSnapshot CurrentSnapshot
        {
            get
            {
                var result = Volatile.Read(ref _snapshot);
                var readCount = Interlocked.Increment(ref _snapshotReadCount);
                if (readCount == 2 && BlockSecondSnapshotRead)
                {
                    SecondReadCaptured.TrySetResult();
                    _releaseSecondRead.Wait(cancellationToken);
                }

                if (readCount == 3)
                {
                    ThirdRead.TrySetResult();
                }

                return result;
            }
        }

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => EmptyUpdates();

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public void ReleaseSecondRead() => _releaseSecondRead.Set();

        public void SetSnapshot(MembershipTableSnapshot value) => Volatile.Write(ref _snapshot, value);

        public Task UpdateLocalStatus(SiloStatus status, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> TryKillSilo(SiloAddress silo, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> TrySuspectSilo(
            SiloAddress silo,
            SiloAddress? indirectProbingSilo,
            CancellationToken cancellationToken) => Task.FromResult(false);

        public Task Refresh(MembershipVersion? targetVersion, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ProcessGossipSnapshot(
            MembershipTableSnapshot value,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpdateIAmAlive(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool CheckHealth(DateTime lastCheckTime, out string reason)
        {
            reason = string.Empty;
            return true;
        }

        public void Participate(ISiloLifecycle lifecycle)
        {
        }

        private static async IAsyncEnumerable<MembershipTableSnapshot> EmptyUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class BlockingDisseminationOptions(CancellationToken cancellationToken)
        : Microsoft.Extensions.Options.IOptions<DisseminationOptions>
    {
        private readonly ManualResetEventSlim _releaseRefresh = new();
        private int _readCount;

        public TaskCompletionSource RefreshBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DisseminationOptions Value
        {
            get
            {
                if (Interlocked.Increment(ref _readCount) == 2)
                {
                    RefreshBlocked.TrySetResult();
                    _releaseRefresh.Wait(cancellationToken);
                }

                return new()
                {
                    Overlay = new()
                    {
                        FanOutFactor = static _ => 2,
                    },
                };
            }
        }

        public void ReleaseRefresh() => _releaseRefresh.Set();
    }
}

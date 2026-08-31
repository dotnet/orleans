using System.Collections.Immutable;
using System.Net;
using System.Threading.Channels;
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
    public async Task ConcurrentValidationsShareOneQualifyingFreshRead()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var firstSilo = CreateSiloAddress();
        var sharedSilo = CreateSiloAddress(port: 11112);
        var snapshot = CreateSnapshot(1);

        var olderValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(firstSilo),
            CancellationToken.None).AsTask();
        var olderRefresh = await membershipManager.WaitForRefreshAttempt();
        var concurrentValidations = Enumerable.Range(0, 100)
            .Select(_ => cache.GetSiloStatuses(
                snapshot,
                SiloAddresses(sharedSilo),
                CancellationToken.None).AsTask())
            .ToArray();

        Assert.Equal(1, membershipManager.SourceRefreshCount);
        olderRefresh.Completion.TrySetResult();

        var sharedRefresh = await membershipManager.WaitForRefreshAttempt();
        Assert.Equal(2, membershipManager.SourceRefreshCount);
        sharedRefresh.Completion.TrySetResult();

        var results = await Task.WhenAll(concurrentValidations).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        Assert.All(results, statuses => Assert.Equal(SiloStatus.Dead, statuses[sharedSilo]));
        Assert.Equal(SiloStatus.Dead, (await olderValidation)[firstSilo]);
        Assert.Equal(2, membershipManager.SourceRefreshCount);
        Assert.Equal(2, membershipManager.CurrentSnapshotReadCount);
    }

    [Fact]
    public async Task CallerArrivingAfterRefreshStartsRequiresNextGeneration()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var firstSilo = CreateSiloAddress();
        var secondSilo = CreateSiloAddress(port: 11112);
        var snapshot = CreateSnapshot(1);

        var firstValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(firstSilo),
            CancellationToken.None).AsTask();
        var firstRefresh = await membershipManager.WaitForRefreshAttempt();
        var secondValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(secondSilo),
            CancellationToken.None).AsTask();

        firstRefresh.Completion.TrySetResult();
        var secondRefresh = await membershipManager.WaitForRefreshAttempt();

        Assert.Equal(SiloStatus.Dead, (await firstValidation)[firstSilo]);
        Assert.False(secondValidation.IsCompleted);
        Assert.Equal(2, membershipManager.SourceRefreshCount);

        secondRefresh.Completion.TrySetResult();
        Assert.Equal(SiloStatus.Dead, (await secondValidation)[secondSilo]);
    }

    [Fact]
    public async Task SharedRefreshCachesDeadSiloBeforeReleasingWaiters()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var firstSilo = CreateSiloAddress();
        var sharedSilo = CreateSiloAddress(port: 11112);
        var snapshot = CreateSnapshot(1);

        var olderValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(firstSilo),
            CancellationToken.None).AsTask();
        var olderRefresh = await membershipManager.WaitForRefreshAttempt();
        var firstWaiter = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(sharedSilo),
            CancellationToken.None).AsTask();
        var secondWaiter = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(sharedSilo),
            CancellationToken.None).AsTask();

        olderRefresh.Completion.TrySetResult();
        var sharedRefresh = await membershipManager.WaitForRefreshAttempt();
        sharedRefresh.Completion.TrySetResult();

        Assert.Equal(SiloStatus.Dead, (await firstWaiter)[sharedSilo]);
        Assert.Equal(SiloStatus.Dead, (await secondWaiter)[sharedSilo]);
        Assert.Equal(
            SiloStatus.Dead,
            (await cache.GetSiloStatuses(
                snapshot,
                SiloAddresses(sharedSilo),
                CancellationToken.None))[sharedSilo]);
        Assert.Equal(SiloStatus.Dead, (await olderValidation)[firstSilo]);
        Assert.Equal(2, membershipManager.SourceRefreshCount);
        Assert.Equal(2, membershipManager.CurrentSnapshotReadCount);
    }

    [Fact]
    public async Task OlderRefreshCannotOverwriteNewerActiveObservation()
    {
        var silo = CreateSiloAddress();
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var olderSnapshot = CreateSnapshot(1);
        var newerSnapshot = CreateSnapshot(2, new ClusterMember(silo, SiloStatus.Active, "silo"));

        var olderValidation = cache.GetSiloStatuses(
            olderSnapshot,
            SiloAddresses(silo),
            CancellationToken.None).AsTask();
        var olderRefresh = await membershipManager.WaitForRefreshAttempt();

        Assert.Equal(
            SiloStatus.Active,
            (await cache.GetSiloStatuses(
                newerSnapshot,
                SiloAddresses(silo),
                CancellationToken.None))[silo]);

        olderRefresh.Completion.TrySetResult();

        Assert.Equal(SiloStatus.Active, (await olderValidation)[silo]);
        Assert.Equal(1, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task ValidationReturnsSnapshotFromQualifyingFreshRead()
    {
        var silo = CreateSiloAddress();
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var validationTask = cache.ValidateSiloStatuses(
            CreateSnapshot(1),
            SiloAddresses(silo),
            CancellationToken.None).AsTask();
        var refresh = await membershipManager.WaitForRefreshAttempt();
        membershipManager.SetCurrentSnapshot(CreateMembershipTableSnapshot(2));

        refresh.Completion.TrySetResult();

        var validation = await validationTask;
        Assert.Equal(new MembershipVersion(2), validation.Snapshot.Version);
        Assert.Equal(SiloStatus.Dead, validation.Statuses[silo]);
        Assert.Equal(1, membershipManager.CurrentSnapshotReadCount);
    }

    [Fact]
    public async Task FreshValidationRecomputesInitiallyKnownSilo()
    {
        var staleSilo = CreateSiloAddress();
        var replacementSilo = CreateSiloAddress(port: 11112);
        var membershipManager = new TestMembershipManager(
            CreateMembershipTableSnapshot(
                2,
                CreateMembershipEntry(staleSilo, SiloStatus.Dead),
                CreateMembershipEntry(replacementSilo, SiloStatus.Active)));
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);

        var validation = await cache.ValidateSiloStatuses(
            CreateSnapshot(1, new ClusterMember(staleSilo, SiloStatus.Active, "stale")),
            SiloAddresses(staleSilo, replacementSilo),
            TestContext.Current.CancellationToken);

        Assert.Equal(new MembershipVersion(2), validation.Snapshot.Version);
        Assert.Equal(SiloStatus.Dead, validation.Statuses[staleSilo]);
        Assert.Equal(SiloStatus.Active, validation.Statuses[replacementSilo]);
        Assert.Equal(1, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task RequiredFreshValidationRefreshesKnownSilo()
    {
        var silo = CreateSiloAddress();
        var membershipManager = new TestMembershipManager(
            CreateMembershipTableSnapshot(
                2,
                CreateMembershipEntry(silo, SiloStatus.Dead)));
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);

        var validation = await cache.ValidateSiloStatuses(
            CreateSnapshot(1, new ClusterMember(silo, SiloStatus.Active, "stale")),
            SiloAddresses(silo),
            TestContext.Current.CancellationToken,
            requireFresh: true);

        Assert.Equal(new MembershipVersion(2), validation.Snapshot.Version);
        Assert.Equal(SiloStatus.Dead, validation.Statuses[silo]);
        Assert.Equal(1, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task FailedRequiredFreshValidationReturnsUnknownForKnownSilo()
    {
        var silo = CreateSiloAddress();
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var validationTask = cache.ValidateSiloStatuses(
            CreateSnapshot(1, new ClusterMember(silo, SiloStatus.Active, "stale")),
            SiloAddresses(silo),
            TestContext.Current.CancellationToken,
            requireFresh: true).AsTask();
        var refresh = await membershipManager.WaitForRefreshAttempt();

        refresh.Completion.TrySetException(new InvalidOperationException("refresh failed"));

        var validation = await validationTask;
        Assert.Equal(SiloStatus.None, validation.Statuses[silo]);
        Assert.Equal(1, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task FailedSharedRefreshAllowsNextValidationToRetry()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var silo = CreateSiloAddress();
        var snapshot = CreateSnapshot(1);

        var failedValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(silo),
            CancellationToken.None).AsTask();
        var failedRefresh = await membershipManager.WaitForRefreshAttempt();
        failedRefresh.Completion.TrySetException(new InvalidOperationException("refresh failed"));

        Assert.Equal(SiloStatus.None, (await failedValidation)[silo]);

        var retryValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(silo),
            CancellationToken.None).AsTask();
        var retryRefresh = await membershipManager.WaitForRefreshAttempt();
        retryRefresh.Completion.TrySetResult();

        Assert.Equal(SiloStatus.Dead, (await retryValidation)[silo]);
        Assert.Equal(2, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelSharedRefresh()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var firstSilo = CreateSiloAddress();
        var sharedSilo = CreateSiloAddress(port: 11112);
        var snapshot = CreateSnapshot(1);

        var olderValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(firstSilo),
            CancellationToken.None).AsTask();
        var olderRefresh = await membershipManager.WaitForRefreshAttempt();
        var survivingWaiter = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(sharedSilo),
            CancellationToken.None).AsTask();
        using var cancellation = new CancellationTokenSource();
        var cancelledWaiter = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(sharedSilo),
            cancellation.Token).AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWaiter);

        olderRefresh.Completion.TrySetResult();
        var sharedRefresh = await membershipManager.WaitForRefreshAttempt();
        Assert.Equal(CancellationToken.None, sharedRefresh.CancellationToken);
        sharedRefresh.Completion.TrySetResult();

        Assert.Equal(SiloStatus.Dead, (await survivingWaiter)[sharedSilo]);
        Assert.Equal(SiloStatus.Dead, (await olderValidation)[firstSilo]);
        Assert.Equal(2, membershipManager.SourceRefreshCount);
    }

    [Fact]
    public async Task ShutdownPreventsQueuedRefreshFromStarting()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1))
        {
            AutoCompleteRefreshes = false,
        };
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var firstSilo = CreateSiloAddress();
        var queuedSilo = CreateSiloAddress(port: 11112);
        var snapshot = CreateSnapshot(1);

        var activeValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(firstSilo),
            CancellationToken.None).AsTask();
        var activeRefresh = await membershipManager.WaitForRefreshAttempt();
        var queuedValidation = cache.GetSiloStatuses(
            snapshot,
            SiloAddresses(queuedSilo),
            CancellationToken.None).AsTask();

        membershipManager.Shutdown();
        activeRefresh.Completion.TrySetResult();

        Assert.Equal(SiloStatus.Dead, (await activeValidation)[firstSilo]);
        Assert.Equal(SiloStatus.None, (await queuedValidation)[queuedSilo]);
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
        var activeSnapshot = CreateSnapshot(2, new ClusterMember(silo, SiloStatus.Active, "silo"));
        Assert.Equal(
            SiloStatus.Active,
            (await cache.GetSiloStatuses(activeSnapshot, SiloAddresses(silo), CancellationToken.None))[silo]);
        membershipManager.SetCurrentSnapshot(CreateMembershipTableSnapshot(3));
        Assert.Equal(
            SiloStatus.Dead,
            (await cache.GetSiloStatuses(CreateSnapshot(3), SiloAddresses(silo), CancellationToken.None))[silo]);
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
    public async Task PreCancelledValidationDoesNotStartSourceRefresh()
    {
        var membershipManager = new TestMembershipManager(CreateMembershipTableSnapshot(1));
        var cache = new UnknownSiloStatusCache(membershipManager, NullLogger<UnknownSiloStatusCache>.Instance);
        var cancellation = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cache.GetSiloStatuses(
                CreateSnapshot(1),
                SiloAddresses(CreateSiloAddress()),
                cancellation).AsTask());
        Assert.Equal(0, membershipManager.SourceRefreshCount);
    }

    private static ClusterMembershipSnapshot CreateSnapshot(long version, params ClusterMember[] members) =>
        new(members.ToImmutableDictionary(member => member.SiloAddress), new MembershipVersion(version));

    private static MembershipTableSnapshot CreateMembershipTableSnapshot(
        long version,
        params MembershipEntry[] entries) =>
        new(
            new MembershipVersion(version),
            entries.ToImmutableDictionary(entry => entry.SiloAddress));

    private static MembershipEntry CreateMembershipEntry(SiloAddress siloAddress, SiloStatus status) =>
        new()
        {
            SiloAddress = siloAddress,
            Status = status,
        };

    private static SiloAddress CreateSiloAddress(int port = 11111) =>
        SiloAddress.New(new IPEndPoint(IPAddress.Loopback, port), 1);

    private static HashSet<SiloAddress> SiloAddresses(params SiloAddress[] addresses) => [.. addresses];

    private sealed class TestMembershipManager(MembershipTableSnapshot snapshot) : IMembershipManager
    {
        private readonly Channel<RefreshAttempt> _refreshAttempts = Channel.CreateUnbounded<RefreshAttempt>();
        private readonly CancellationTokenSource _shutdown = new();
        private MembershipTableSnapshot _currentSnapshot = snapshot;
        private int _currentSnapshotReadCount;
        private int _sourceRefreshCount;

        public bool AutoCompleteRefreshes { get; init; } = true;

        public int CurrentSnapshotReadCount => Volatile.Read(ref _currentSnapshotReadCount);

        public int SourceRefreshCount => Volatile.Read(ref _sourceRefreshCount);

        public MembershipTableSnapshot CurrentSnapshot
        {
            get
            {
                Interlocked.Increment(ref _currentSnapshotReadCount);
                return Volatile.Read(ref _currentSnapshot);
            }
        }

        public IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates => GetMembershipUpdates();

        public SiloStatus LocalSiloStatus => SiloStatus.Active;

        public async Task Refresh(
            MembershipVersion? targetVersion,
            CancellationToken cancellationToken,
            bool requireFresh = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _shutdown.Token.ThrowIfCancellationRequested();
            if (!requireFresh)
            {
                return;
            }

            var attempt = new RefreshAttempt(
                Interlocked.Increment(ref _sourceRefreshCount),
                cancellationToken);
            Assert.True(_refreshAttempts.Writer.TryWrite(attempt));
            if (AutoCompleteRefreshes)
            {
                attempt.Completion.TrySetResult();
            }

            await attempt.Completion.Task.WaitAsync(cancellationToken);
        }

        public void Shutdown() => _shutdown.Cancel();

        public void SetCurrentSnapshot(MembershipTableSnapshot snapshot) =>
            Volatile.Write(ref _currentSnapshot, snapshot);

        public async Task<RefreshAttempt> WaitForRefreshAttempt() =>
            await _refreshAttempts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30));

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

    private sealed record RefreshAttempt(
        int Number,
        CancellationToken CancellationToken)
    {
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

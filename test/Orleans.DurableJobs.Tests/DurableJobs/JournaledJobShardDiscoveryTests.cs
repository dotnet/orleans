using System.Diagnostics;
using System.Net;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

public partial class JournaledJobShardManagerTests
{
    [Fact]
    public async Task Discovery_UnorderedCatalogClaimsOldestFirstIncludingYearsOverdue()
    {
        await using var fixture = new DiscoveryFixture();
        var oldest = await fixture.AddShardAsync("oldest", fixture.Now.AddYears(-8));
        var middle = await fixture.AddShardAsync("middle", fixture.Now.AddDays(-1));
        var youngest = await fixture.AddShardAsync("youngest", fixture.Now);
        fixture.Catalog.Ids.AddRange([youngest, oldest, middle]);

        AssertAssignedIds([oldest, middle], await fixture.DiscoverAsync(maxNewClaims: 2));
        Assert.Equal(new[] { oldest, middle, youngest }, fixture.Storage.MetadataReads);
        Assert.Equal(1, fixture.Catalog.ListCalls);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);

        AssertAssignedIds([oldest, middle, youngest], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(new[] { oldest, middle, youngest, oldest, middle, youngest }, fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-7)]
    public async Task Discovery_InclusiveUtcTickBoundExcludesFutureMetadata(int offsetHours)
    {
        await using var fixture = new DiscoveryFixture();
        var horizon = fixture.Horizon.AddTicks(1234567).ToOffset(TimeSpan.FromHours(offsetHours));
        var previous = await fixture.AddShardAsync("previous", horizon.AddTicks(-1));
        var boundaryA = await fixture.AddShardAsync("boundary-a", horizon);
        var boundaryZ = await fixture.AddShardAsync("boundary-z", horizon.ToUniversalTime());
        var nextTick = await fixture.AddShardAsync("next-tick", horizon.AddTicks(1));
        var farFuture = await fixture.AddShardAsync("far-future", horizon.AddYears(10));
        fixture.Catalog.Ids.AddRange([farFuture, nextTick, boundaryZ, previous, boundaryA]);
        fixture.Storage.BeforeMetadataRead = (id, _) =>
        {
            Assert.NotEqual(nextTick, id);
            Assert.NotEqual(farFuture, id);
            return ValueTask.CompletedTask;
        };

        AssertAssignedIds([previous, boundaryA, boundaryZ], await fixture.DiscoverAsync(horizon: horizon));
        Assert.Equal(new[] { previous, boundaryA, boundaryZ }, fixture.Storage.MetadataReads);
        Assert.Equal(3, fixture.Catalog.YieldedIds);
        Assert.Equal(4, fixture.Catalog.MoveNextCalls);
        var request = Assert.Single(fixture.Catalog.Requests);
        Assert.Equal(JobShardId.StoragePrefix.Value + "/", request.Prefix.Value);
        Assert.Equal(JobShardId.GetMaxJournalId(horizon.ToUniversalTime()), request.MaxId);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_EmptyAndFreshSweepsObserveEarlierInsertionsAndLaterHorizon()
    {
        await using var fixture = new DiscoveryFixture();
        Assert.Empty(await fixture.DiscoverAsync());
        Assert.Empty(fixture.Storage.MetadataReads);
        Assert.Equal(1, fixture.Catalog.MoveNextCalls);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);

        var current = await fixture.AddShardAsync("current", fixture.Now);
        var future = await fixture.AddShardAsync("future", fixture.Horizon.AddTicks(1));
        fixture.Catalog.Ids.AddRange([future, current]);
        AssertAssignedIds([current], await fixture.DiscoverAsync());

        var inserted = await fixture.AddShardAsync("inserted", fixture.Now.AddYears(-3));
        fixture.Catalog.Ids.Add(inserted);
        AssertAssignedIds([inserted, current], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(new[] { current, inserted, current }, fixture.Storage.MetadataReads);

        var laterHorizon = fixture.Horizon.AddTicks(1);
        AssertAssignedIds([inserted, current, future], await fixture.DiscoverAsync(horizon: laterHorizon));
        Assert.Equal(new[] { current, inserted, current, inserted, current, future }, fixture.Storage.MetadataReads);
        Assert.Equal(4, fixture.Catalog.ListCalls);
        Assert.Equal(4, fixture.Catalog.DisposeCalls);
        Assert.Equal(
            new[]
            {
                JobShardId.GetMaxJournalId(fixture.Horizon),
                JobShardId.GetMaxJournalId(fixture.Horizon),
                JobShardId.GetMaxJournalId(fixture.Horizon),
                JobShardId.GetMaxJournalId(laterHorizon)
            },
            fixture.Catalog.Requests.Select(request => request.MaxId));
    }

    [Fact]
    public async Task Discovery_ClaimBudgetCountsOnlyClaimsAndStillReturnsLocalShards()
    {
        await using var fixture = new DiscoveryFixture();
        var firstLocal = await fixture.AddShardAsync("first-local", fixture.Now.AddHours(-3), fixture.Silo);
        var olderOrphan = await fixture.AddShardAsync("older-orphan", fixture.Now.AddHours(-2));
        var youngerOrphan = await fixture.AddShardAsync("younger-orphan", fixture.Now);
        var lastLocal = await fixture.AddShardAsync("last-local", fixture.Now.AddMinutes(30), fixture.Silo);
        fixture.Catalog.Ids.AddRange([lastLocal, youngerOrphan, olderOrphan, firstLocal]);

        AssertAssignedIds([firstLocal, lastLocal], await fixture.DiscoverAsync(maxNewClaims: 0));
        Assert.Equal(new[] { firstLocal, olderOrphan, youngerOrphan, lastLocal }, fixture.Storage.MetadataReads);

        AssertAssignedIds([firstLocal, olderOrphan, lastLocal], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(
            new[] { firstLocal, olderOrphan, youngerOrphan, lastLocal, firstLocal, olderOrphan, youngerOrphan, lastLocal },
            fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
        var unclaimed = await fixture.Storage.CreateStorage(youngerOrphan).GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(unclaimed);
        Assert.False(unclaimed.Properties.ContainsKey("DurableJobsOwner"));
    }

    [Fact]
    public async Task Discovery_MetadataFailurePreservesYieldedAssignmentAndRetriesFresh()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now.AddMinutes(-2));
        var failing = await fixture.AddShardAsync("failing", fixture.Now.AddMinutes(-1));
        var tail = await fixture.AddShardAsync("tail", fixture.Now);
        fixture.Catalog.Ids.AddRange([tail, failing, first]);
        var failure = new InvalidOperationException("Metadata unavailable");
        fixture.Storage.BeforeMetadataRead = (id, _) => id == failing ? throw failure : ValueTask.CompletedTask;

        IJobShard retained;
        await using (var discovery = fixture.DiscoverStreamAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await discovery.MoveNextAsync());
            retained = discovery.Current;
            AssertAssignedIds([first], [retained]);
            Assert.Equal(new[] { first }, fixture.Storage.MetadataReads);
            Assert.Equal(1, fixture.Catalog.DisposeCalls);
            Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => discovery.MoveNextAsync().AsTask()));
        }

        Assert.Equal(new[] { first, failing }, fixture.Storage.MetadataReads);
        Assert.Equal(0, await retained.GetJobCountAsync());
        fixture.Storage.BeforeMetadataRead = null;
        var retry = await fixture.DiscoverAsync(maxNewClaims: 2);
        AssertAssignedIds([first, failing, tail], retry);
        Assert.Same(retained, retry[0]);
        Assert.Equal(new[] { first, failing, first, failing, tail }, fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_CancellationDuringListingDisposesEnumeratorAndRetriesFresh()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now);
        fixture.Catalog.Ids.Add(first);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DiscoverWithCancellationAsync(canceled.Token));
        Assert.Equal(0, fixture.Catalog.ListCalls);

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var moveNextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Catalog.BeforeMoveNext = async (_, token) =>
        {
            Assert.Equal(lifetime.Token, token);
            moveNextStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var discovery = fixture.DiscoverWithCancellationAsync(lifetime.Token);
        try
        {
            await moveNextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            lifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        }

        Assert.Empty(fixture.Storage.MetadataReads);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);
        fixture.Catalog.BeforeMoveNext = null;
        AssertAssignedIds([first], await fixture.DiscoverAsync());
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_CancellationBetweenAssignmentsStopsBeforeNextMetadataRead()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now.AddMinutes(-1));
        var next = await fixture.AddShardAsync("next", fixture.Now);
        fixture.Catalog.Ids.AddRange([next, first]);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using (var discovery = fixture.DiscoverStreamAsync(lifetime.Token).GetAsyncEnumerator(lifetime.Token))
        {
            Assert.True(await discovery.MoveNextAsync());
            AssertAssignedIds([first], [discovery.Current]);
            lifetime.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery.MoveNextAsync().AsTask());
        }

        Assert.Equal(new[] { first }, fixture.Storage.MetadataReads);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);
        AssertAssignedIds([first, next], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(new[] { first, first, next }, fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_StoppingConsumerDoesNotRetainEnumerationOrTraversalPosition()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now.AddMinutes(-1));
        var tail = await fixture.AddShardAsync("tail", fixture.Now);
        fixture.Catalog.Ids.AddRange([tail, first]);
        await using (var discovery = fixture.DiscoverStreamAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken))
        {
            Assert.True(await discovery.MoveNextAsync());
            AssertAssignedIds([first], [discovery.Current]);
            Assert.Equal(1, fixture.Catalog.DisposeCalls);
        }

        Assert.Equal(new[] { first }, fixture.Storage.MetadataReads);
        var inserted = await fixture.AddShardAsync("inserted", fixture.Now.AddYears(-1));
        fixture.Catalog.Ids.Add(inserted);
        AssertAssignedIds([inserted, first], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(new[] { first, inserted, first, tail }, fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_AwaitsCatalogDisposalBeforeReadingMetadata()
    {
        await using var fixture = new DiscoveryFixture();
        var due = await fixture.AddShardAsync("due", fixture.Now);
        fixture.Catalog.Ids.Add(due);
        var disposalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisposal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Catalog.OnDispose = async () =>
        {
            disposalStarted.SetResult();
            await allowDisposal.Task.WaitAsync(TestContext.Current.CancellationToken);
        };

        var discovery = fixture.DiscoverAsync();
        try
        {
            await disposalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(discovery.IsCompleted);
            Assert.Empty(fixture.Storage.MetadataReads);
        }
        finally
        {
            allowDisposal.TrySetResult();
            await discovery;
        }

        AssertAssignedIds([due], await discovery);
        Assert.Equal(new[] { due }, fixture.Storage.MetadataReads);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_ListingFailureSurfacesBeforeMetadataAndRetriesFresh()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now.AddMinutes(-1));
        var tail = await fixture.AddShardAsync("tail", fixture.Now);
        fixture.Catalog.Ids.AddRange([first, tail]);
        var failure = new InvalidOperationException("Listing unavailable");
        fixture.Catalog.BeforeMoveNext = (index, _) => index == 1 ? throw failure : ValueTask.CompletedTask;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DiscoverAsync()));
        Assert.Empty(fixture.Storage.MetadataReads);
        Assert.Equal(1, fixture.Catalog.YieldedIds);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);

        var inserted = await fixture.AddShardAsync("inserted", fixture.Now.AddYears(-1));
        fixture.Catalog.Ids.Add(inserted);
        fixture.Catalog.BeforeMoveNext = null;
        AssertAssignedIds([inserted, first, tail], await fixture.DiscoverAsync());
        Assert.Equal(new[] { inserted, first, tail }, fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_MembershipChangesUseCurrentStatusForEachCandidate()
    {
        await using var fixture = new DiscoveryFixture();
        var owner = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5101), 0);
        fixture.Membership.SetSiloStatus(owner, SiloStatus.Active);
        var first = await fixture.AddShardAsync("first", fixture.Now.AddMinutes(-1), owner);
        var second = await fixture.AddShardAsync("second", fixture.Now, owner);
        fixture.Catalog.Ids.AddRange([second, first]);
        fixture.Storage.BeforeMetadataRead = (id, _) =>
        {
            fixture.Membership.SetSiloStatus(owner, id == first ? SiloStatus.Dead : SiloStatus.Active);
            return ValueTask.CompletedTask;
        };

        AssertAssignedIds([first], await fixture.DiscoverAsync());
        Assert.Equal(new[] { first, second }, fixture.Storage.MetadataReads);

        fixture.Storage.BeforeMetadataRead = null;
        fixture.Membership.SetSiloStatus(owner, SiloStatus.Dead);
        AssertAssignedIds([first, second], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(new[] { first, second, first, second }, fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        var metadata = await fixture.Storage.CreateStorage(second).GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Silo.ToParsableString(), metadata!.Properties["DurableJobsOwner"]);
        Assert.Equal("1", metadata.Properties["DurableJobsAdoptedCount"]);
    }

    [Fact]
    public async Task Discovery_DuplicateIdentitiesReadMetadataAndAssignOnlyOnce()
    {
        await using var fixture = new DiscoveryFixture();
        var due = await fixture.AddShardAsync("due", fixture.Now);
        fixture.Catalog.Ids.AddRange([due, due, due]);

        AssertAssignedIds([due], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(new[] { due }, fixture.Storage.MetadataReads);
        Assert.Equal(3, fixture.Catalog.YieldedIds);
        Assert.Equal(1, fixture.Catalog.ListCalls);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);
    }

    [Fact]
    public async Task Discovery_VolatileCatalogHonorsTimestampBound()
    {
        await using var fixture = new DiscoveryFixture(useStorageCatalog: true);
        var due = await fixture.AddShardAsync("due", fixture.Now);
        await fixture.AddShardAsync("future", fixture.Horizon.AddTicks(1));

        AssertAssignedIds([due], await fixture.DiscoverAsync(maxNewClaims: 1));
        Assert.Equal(new[] { due }, fixture.Storage.MetadataReads);
    }

    [Fact]
    public async Task Assignment_PublicApiCollectsFreshBoundedOldestFirstSweep()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now.AddMinutes(-1), fixture.Silo);
        var second = await fixture.AddShardAsync("second", fixture.Now, fixture.Silo);
        var future = await fixture.AddShardAsync("future", fixture.Horizon.AddTicks(1), fixture.Silo);
        fixture.Catalog.Ids.AddRange([future, second, first]);

        var initial = await fixture.AssignAsync();
        AssertAssignedIds([first, second], initial);
        var inserted = await fixture.AddShardAsync("inserted", fixture.Now.AddYears(-1), fixture.Silo);
        fixture.Catalog.Ids.Add(inserted);
        var next = await fixture.AssignAsync();
        AssertAssignedIds([inserted, first, second], next);
        Assert.Same(initial[0], next[1]);
        Assert.Same(initial[1], next[2]);
        Assert.Equal(new[] { first, second, inserted, first, second }, fixture.Storage.MetadataReads);
        Assert.Equal(2, fixture.Catalog.ListCalls);
        Assert.Equal(2, fixture.Catalog.DisposeCalls);
        Assert.All(fixture.Catalog.Requests, request => Assert.Equal(JobShardId.GetMaxJournalId(fixture.Horizon), request.MaxId));
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(30, 0)]
    [InlineData(1, 3)]
    [InlineData(30, 3)]
    public async Task Discovery_DefunctPredecessorWorkloadBoundsCandidateMetadataWork(int futureBuckets, int maxNewClaims)
    {
        const int PredecessorCount = 256;
        await using var fixture = new DiscoveryFixture();
        var dueIds = new List<JournalId>();
        for (var predecessor = 0; predecessor < PredecessorCount; predecessor++)
        {
            var owner = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5200 + predecessor), 0);
            fixture.Membership.SetSiloStatus(owner, SiloStatus.Dead);
            var due = await fixture.AddShardAsync(
                $"due-{predecessor}", fixture.Now.AddYears(-10).AddHours(predecessor), owner);
            dueIds.Add(due);
            fixture.Catalog.Ids.Add(due);
            for (var bucket = 0; bucket < futureBuckets; bucket++)
            {
                fixture.Catalog.Ids.Add(await fixture.AddShardAsync(
                    $"future-{predecessor}-{bucket}",
                    fixture.Horizon.AddYears(1).AddDays(bucket).AddTicks(predecessor),
                    owner));
            }
        }

        fixture.Catalog.Ids.Reverse();
        var stopwatch = Stopwatch.StartNew();
        var assigned = await fixture.DiscoverAsync(maxNewClaims);
        stopwatch.Stop();

        AssertAssignedIds(dueIds.Take(maxNewClaims), assigned);
        Assert.Equal(dueIds, fixture.Storage.MetadataReads);
        Assert.Equal(PredecessorCount, fixture.Storage.MetadataReads.Count);
        Assert.Equal(PredecessorCount, fixture.Catalog.YieldedIds);
        Assert.Equal(PredecessorCount + 1, fixture.Catalog.MoveNextCalls);
        Assert.Equal(1, fixture.Catalog.ListCalls);
        Assert.Equal(1, fixture.Catalog.DisposeCalls);
        var request = Assert.Single(fixture.Catalog.Requests);
        Assert.Equal(JobShardId.StoragePrefix.Value + "/", request.Prefix.Value);
        Assert.Equal(JobShardId.GetMaxJournalId(fixture.Horizon), request.MaxId);
        var unboundedIdentityCount = PredecessorCount * (futureBuckets + 1);
        Assert.Equal(unboundedIdentityCount, fixture.Catalog.Ids.Count);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Workload: predecessor hosts={PredecessorCount}, due shards per predecessor=1, total due shards={dueIds.Count}, " +
            $"future time buckets={futureBuckets}, future shards per bucket={PredecessorCount}, " +
            $"total future shards={fixture.Catalog.Ids.Count - dueIds.Count}, total catalog shards={fixture.Catalog.Ids.Count}. " +
            $"Per recovering silo (one silo: {fixture.Silo}): hypothetical unbounded candidate identities={unboundedIdentityCount}, " +
            $"actual bounded candidate identities={fixture.Catalog.YieldedIds}, actual metadata reads={fixture.Storage.MetadataReads.Count}, " +
            $"actual new claims={assigned.Count}, claim budget={maxNewClaims}, catalog enumerations={fixture.Catalog.ListCalls}, " +
            $"elapsed sweep={stopwatch.Elapsed.TotalMilliseconds:F3} ms (diagnostic only; no timing threshold). " +
            "Candidate and metadata counts measure consumer work; they do not measure or imply reduced provider-internal LIST scanning.");
    }

    private static void AssertAssignedIds(IEnumerable<JournalId> expected, IEnumerable<IJobShard> actual)
        => Assert.Equal(expected.Select(id => JobShardId.FromJournalId(id).Value), actual.Select(shard => shard.Id));
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration.Internal;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestSuite("BVT")]
[TestProvider("None")]
[TestArea("DurableJobs")]
[TestCategory("BVT"), TestCategory("DurableJobs")]
public partial class JournaledJobShardManagerTests
{
    [Fact]
    public async Task Discovery_BoundsMostlyIneligibleIdentitiesAndResumes()
    {
        await using var fixture = new DiscoveryFixture();
        var other = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5101), 0);
        fixture.Membership.SetSiloStatus(other, SiloStatus.Active);
        var identities = new List<JournalId>();
        for (var i = 0; i < JournaledJobShardManager.CatalogPageSize; i++)
        {
            if (i % 4 == 3)
            {
                identities.Add(new JobShardId($"missing-{i}").ToJournalId());
                continue;
            }

            identities.Add(await fixture.AddShardAsync(
                $"skip-{i}", i % 4 == 0 ? fixture.Horizon.AddTicks(1) : fixture.Now,
                owner: other, poisoned: i % 4 == 1));
        }

        var due = await fixture.AddShardAsync("due", fixture.Now);
        fixture.Catalog.ReadPage = token => token is null
            ? new() { JournalIds = identities, ContinuationToken = "tail" }
            : new() { JournalIds = [due] };

        Assert.Empty(await fixture.DiscoverAsync());
        Assert.Equal(JournaledJobShardManager.CatalogPageSize, fixture.Storage.MetadataReads.Count);
        Assert.Equal(new string?[] { null }, fixture.Catalog.Tokens);
        Assert.True(fixture.Manager.HasMoreCatalogWork);

        Assert.Equal("due", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Equal(identities.Append(due), fixture.Storage.MetadataReads);
        Assert.Equal(new string?[] { null, "tail" }, fixture.Catalog.Tokens);
        Assert.False(fixture.Manager.HasMoreCatalogWork);
        Assert.Equal(0, fixture.Catalog.ListCalls);
    }

    [Fact]
    public async Task Discovery_EmptyNonterminalPagesEachConsumeATurn()
    {
        await using var fixture = new DiscoveryFixture();
        var due = await fixture.AddShardAsync("due", fixture.Now);
        fixture.Catalog.ReadPage = token => token switch
        {
            null => new() { JournalIds = [], ContinuationToken = "empty" },
            "empty" => new() { JournalIds = [], ContinuationToken = "due" },
            _ => new() { JournalIds = [due] }
        };

        Assert.Empty(await fixture.DiscoverAsync());
        Assert.Single(fixture.Catalog.Tokens);
        Assert.True(fixture.Manager.HasMoreCatalogWork);
        Assert.Empty(await fixture.DiscoverAsync());
        Assert.Equal(2, fixture.Catalog.Tokens.Count);
        Assert.Empty(fixture.Storage.MetadataReads);
        Assert.True(fixture.Manager.HasMoreCatalogWork);
        Assert.Equal("due", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Equal(new string?[] { null, "empty", "due" }, fixture.Catalog.Tokens);
        Assert.False(fixture.Manager.HasMoreCatalogWork);
    }

    [Fact]
    public async Task Discovery_ClaimBudgetExhaustionStillVisitsLocalShardsAndCompletesSweep()
    {
        await using var fixture = new DiscoveryFixture();
        var orphan = await fixture.AddShardAsync("orphan", fixture.Now);
        var local = await fixture.AddShardAsync("local", fixture.Now, fixture.Silo);
        fixture.Catalog.ReadPage = _ => new() { JournalIds = [orphan, local] };

        Assert.Equal("local", Assert.Single(await fixture.DiscoverAsync(maxNewClaims: 0)).Id);
        Assert.Equal(new[] { orphan, local }, fixture.Storage.MetadataReads);
        Assert.False(fixture.Manager.HasMoreCatalogWork);
        Assert.Equal(new[] { "orphan", "local" }, (await fixture.DiscoverAsync(maxNewClaims: 1)).Select(shard => shard.Id));
        Assert.Equal(new string?[] { null, null }, fixture.Catalog.Tokens);
    }

    [Fact]
    public async Task Discovery_MetadataFailureSurfacesAndNextTurnContinuesBeforeRetryingNextSweep()
    {
        await using var fixture = new DiscoveryFixture();
        var failing = await fixture.AddShardAsync("failing", fixture.Now);
        var next = await fixture.AddShardAsync("next", fixture.Now);
        fixture.Catalog.ReadPage = _ => new() { JournalIds = [failing, next] };
        var failure = new InvalidOperationException("Metadata unavailable");
        fixture.Storage.BeforeMetadataRead = (id, _) => id == failing ? throw failure : ValueTask.CompletedTask;

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DiscoverAsync()));
        Assert.True(fixture.Manager.HasMoreCatalogWork);
        Assert.Equal("next", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Single(fixture.Catalog.Tokens);
        Assert.False(fixture.Manager.HasMoreCatalogWork);

        fixture.Storage.BeforeMetadataRead = null;
        Assert.Equal(new[] { "failing", "next" }, (await fixture.DiscoverAsync()).Select(shard => shard.Id));
        Assert.Equal(new[] { failing, next, failing, next }, fixture.Storage.MetadataReads);
        Assert.Equal(new string?[] { null, null }, fixture.Catalog.Tokens);
    }

    [Fact]
    public async Task Discovery_CancellationBetweenCandidatesRetainsRemainingPage()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now);
        var second = await fixture.AddShardAsync("second", fixture.Now);
        fixture.Catalog.ReadPage = _ => new() { JournalIds = [first, second] };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DiscoverWithCancellationAsync(cancellation.Token));
        Assert.Empty(fixture.Catalog.Tokens);

        using var midPageCancellation = new CancellationTokenSource();
        fixture.Storage.BeforeMetadataRead = (_, token) =>
        {
            midPageCancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DiscoverWithCancellationAsync(midPageCancellation.Token));
        Assert.Equal(new[] { first }, fixture.Storage.MetadataReads);
        Assert.True(fixture.Manager.HasMoreCatalogWork);
        fixture.Storage.BeforeMetadataRead = null;
        Assert.Equal("second", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Single(fixture.Catalog.Tokens);
        Assert.Equal(new[] { "first", "second" }, (await fixture.DiscoverAsync()).Select(shard => shard.Id));
    }

    [Fact]
    public async Task Discovery_PersistentMetadataFailurePreservesEarlierAndLaterAssignments()
    {
        await using var fixture = new DiscoveryFixture();
        var claimed = await fixture.AddShardAsync("claimed", fixture.Now);
        var failing = await fixture.AddShardAsync("failing", fixture.Now);
        var tail = await fixture.AddShardAsync("tail", fixture.Now);
        fixture.Catalog.ReadPage = _ => new() { JournalIds = [claimed, failing, tail] };
        fixture.Storage.BeforeMetadataRead = (id, _) => id == failing
            ? throw new InvalidOperationException("Metadata unavailable")
            : ValueTask.CompletedTask;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DiscoverAsync());
        Assert.Equal("claimed", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Equal(new[] { claimed, failing }, fixture.Storage.MetadataReads);
        Assert.Equal("tail", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.False(fixture.Manager.HasMoreCatalogWork);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DiscoverAsync(maxNewClaims: 0));
        Assert.Equal("claimed", Assert.Single(await fixture.DiscoverAsync(maxNewClaims: 0)).Id);
        Assert.Equal("tail", Assert.Single(await fixture.DiscoverAsync(maxNewClaims: 0)).Id);
        Assert.Equal(new[] { claimed, failing, tail, claimed, failing, tail }, fixture.Storage.MetadataReads);
        Assert.False(fixture.Manager.HasMoreCatalogWork);
    }

    [Fact]
    public async Task Discovery_PageReadFailurePreservesOpaqueContinuation()
    {
        await using var fixture = new DiscoveryFixture();
        var due = await fixture.AddShardAsync("due", fixture.Now);
        var fail = true;
        fixture.Catalog.ReadPage = token => token is null
            ? new() { JournalIds = [], ContinuationToken = "opaque:next" }
            : fail ? throw new OperationCanceledException() : new() { JournalIds = [due] };

        Assert.Empty(await fixture.DiscoverAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.DiscoverAsync());
        Assert.True(fixture.Manager.HasMoreCatalogWork);
        fail = false;
        Assert.Equal("due", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Equal(new string?[] { null, "opaque:next", "opaque:next" }, fixture.Catalog.Tokens);
    }

    [Fact]
    public async Task Discovery_MembershipChangesPreserveCursorAndUseCurrentOwnerStatus()
    {
        await using var fixture = new DiscoveryFixture();
        var owner = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5101), 0);
        fixture.Membership.SetSiloStatus(owner, SiloStatus.Dead);
        var first = await fixture.AddShardAsync("first", fixture.Now, owner);
        var second = await fixture.AddShardAsync("second", fixture.Now, owner);
        fixture.Catalog.ReadPage = token => token is null
            ? new() { JournalIds = [first], ContinuationToken = "second" }
            : new() { JournalIds = [second] };
        fixture.Storage.BeforeMetadataRead = (_, _) =>
        {
            fixture.Membership.SetSiloStatus(owner, SiloStatus.Active);
            return ValueTask.CompletedTask;
        };

        Assert.Empty(await fixture.DiscoverAsync());
        fixture.Storage.BeforeMetadataRead = null;
        fixture.Membership.SetSiloStatus(owner, SiloStatus.Dead);
        Assert.Equal("second", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Equal(new string?[] { null, "second" }, fixture.Catalog.Tokens);
        Assert.Equal("first", Assert.Single(await fixture.DiscoverAsync()).Id);
        var metadata = await fixture.Storage.CreateStorage(second).GetMetadataAsync(TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Silo.ToParsableString(), metadata!.Properties["DurableJobsOwner"]);
        Assert.Equal("1", metadata.Properties["DurableJobsAdoptedCount"]);
    }

    [Fact]
    public async Task Discovery_NewSweepObservesInsertionBehindCursorAndFutureEligibility()
    {
        await using var fixture = new DiscoveryFixture();
        var future = await fixture.AddShardAsync("future", fixture.Horizon.AddMinutes(1));
        var tail = await fixture.AddShardAsync("tail", fixture.Now);
        JournalId? inserted = null;
        fixture.Catalog.ReadPage = token => token is null
            ? new() { JournalIds = inserted is { } id ? [id, future] : [future], ContinuationToken = "tail" }
            : new() { JournalIds = [tail] };

        Assert.Empty(await fixture.DiscoverAsync());
        inserted = await fixture.AddShardAsync("behind", fixture.Now);
        Assert.Equal("tail", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.False(fixture.Manager.HasMoreCatalogWork);
        Assert.Equal(new[] { "behind", "future" }, (await fixture.DiscoverAsync(
            horizon: fixture.Horizon.AddMinutes(1))).Select(shard => shard.Id));
        Assert.Equal(new string?[] { null, "tail", null }, fixture.Catalog.Tokens);
    }

    [Fact]
    public async Task Discovery_DuplicateIdentitiesConsumeBudgetAndReuseLocalShard()
    {
        await using var fixture = new DiscoveryFixture();
        var due = await fixture.AddShardAsync("due", fixture.Now);
        fixture.Catalog.ReadPage = _ => new() { JournalIds = [due, due] };

        var result = await fixture.DiscoverAsync(maxNewClaims: 1);
        Assert.Equal(2, result.Count);
        Assert.Same(result[0], result[1]);
        Assert.Equal(new[] { due, due }, fixture.Storage.MetadataReads);
        Assert.False(fixture.Manager.HasMoreCatalogWork);
    }

    [Fact]
    public async Task Discovery_LegacyCatalogRetainsFullAssignment()
    {
        var storage = new CountingJournalStorageProvider(delayAppends: false);
        using var services = CreateServices(storage);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5100), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);
        var manager = CreateManager(services, membership, silo);
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        await storage.CreateStorage(new JobShardId("legacy").ToJournalId()).CreateIfNotExistsAsync(
            new Dictionary<string, string>
            {
                ["DurableJobsMinDueTime"] = now.ToString("O"),
                ["DurableJobsMaxDueTime"] = now.AddHours(1).ToString("O")
            }, TestContext.Current.CancellationToken);

        await using var shard = Assert.Single(await manager.DiscoverJobShardsAsync(now, 1, TestContext.Current.CancellationToken));
        Assert.Equal("legacy", shard.Id);
        Assert.False(manager.HasMoreCatalogWork);
    }

    [Fact]
    public async Task Assignment_PublicFullScanRemainsIndependentOfRuntimeCursor()
    {
        await using var fixture = new DiscoveryFixture();
        var first = await fixture.AddShardAsync("first", fixture.Now, fixture.Silo);
        var second = await fixture.AddShardAsync("second", fixture.Now, fixture.Silo);
        fixture.Catalog.ReadPage = token => token is null
            ? new() { JournalIds = [first], ContinuationToken = "second" }
            : new() { JournalIds = [second] };

        Assert.Equal("first", Assert.Single(await fixture.DiscoverAsync()).Id);
        var full = await fixture.AssignAsync();
        Assert.Equal(new[] { "first", "second" }, full.Select(shard => shard.Id).Order());
        Assert.Equal(1, fixture.Catalog.ListCalls);
        Assert.Equal("second", Assert.Single(await fixture.DiscoverAsync()).Id);
        Assert.Equal(new string?[] { null, "second" }, fixture.Catalog.Tokens);
    }

    [Fact]
    public async Task ReleasedShard_IsClaimedClosedAndReplayedFromJournal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5001), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var end = start.AddHours(1);
        var shard = await manager1.CreateShardAsync(
            start,
            end,
            new Dictionary<string, string> { ["Purpose"] = "JournaledManagerTest" },
            cancellationToken);

        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = new Dictionary<string, string> { ["Kind"] = "Replay" }
        }, cancellationToken);
        Assert.NotNull(scheduled);

        await manager1.UnregisterShardAsync(shard, cancellationToken);

        var claimed = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken);
        var claimedShard = Assert.Single(claimed);
        Assert.True(claimedShard.IsAddingCompleted);
        Assert.Equal("JournaledManagerTest", claimedShard.Metadata!["Purpose"]);

        var rejected = await claimedShard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target2"),
            JobName = "new-job",
            DueTime = DateTimeOffset.UtcNow,
            Metadata = null
        }, cancellationToken);
        Assert.Null(rejected);

        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in claimedShard.ConsumeDurableJobsAsync().WithCancellation(cancellationToken))
        {
            consumed.Add(jobContext);
            await claimedShard.RemoveJobAsync(jobContext.Job.Id, cancellationToken);
        }

        var replayed = Assert.Single(consumed);
        Assert.Equal(scheduled.Id, replayed.Job.Id);
        Assert.Equal("Replay", replayed.Job.Metadata!["Kind"]);
        Assert.Equal(1, replayed.DequeueCount);

        await manager2.UnregisterShardAsync(claimedShard, cancellationToken);
        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken));
    }

    [Fact]
    public async Task EmptyShard_IsDeletedWhenUnregistered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);

        var manager = CreateManager(services, membership, silo);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var shard = await manager.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "EmptyShardDelete" },
            cancellationToken);
        var storageId = ((JournaledJobShard)shard).StorageId;

        Assert.NotNull(await storageProvider.CreateStorage(storageId).GetMetadataAsync(cancellationToken));

        await manager.UnregisterShardAsync(shard, cancellationToken);

        Assert.Null(await storageProvider.CreateStorage(storageId).GetMetadataAsync(cancellationToken));
        Assert.Empty(await ToListAsync(storageProvider.ListAsync(
            new() { Prefix = JobShardId.StoragePrefix },
            cancellationToken), cancellationToken));
    }

    [Fact]
    public async Task ClosedLocalShard_CanStillPersistRemovals()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5015), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);

        var manager = CreateManager(services, membership, silo);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "ClosedLocalShard" },
            cancellationToken);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "closed-local-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, cancellationToken);
        Assert.NotNull(scheduled);

        await shard.MarkAsCompleteAsync(cancellationToken);
        await manager.UnregisterShardAsync(shard, cancellationToken);

        var reopenedManager = CreateManager(services, membership, silo);
        var reopened = await reopenedManager.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken);
        shard = Assert.Single(reopened);
        Assert.True(shard.IsAddingCompleted);

        var rejected = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target2"),
            JobName = "rejected-job",
            DueTime = DateTimeOffset.UtcNow,
            Metadata = null
        }, cancellationToken);
        Assert.Null(rejected);

        await foreach (var jobContext in shard.ConsumeDurableJobsAsync().WithCancellation(cancellationToken))
        {
            Assert.Equal(scheduled.Id, jobContext.Job.Id);
            Assert.Equal(
                DurableJobMutationResult.Applied,
                await shard.RemoveJobAsync(jobContext.Job.Id, cancellationToken));
        }

        Assert.Equal(0, await shard.GetJobCountAsync());
        await reopenedManager.UnregisterShardAsync(shard, cancellationToken);
    }

    [Fact]
    public async Task ConcurrentSchedules_ArePersistedInStorageBatches()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const int JobCount = 32;
        var storageProvider = new CountingJournalStorageProvider(delayAppends: true);
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5016), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);
        var manager = CreateManager(services, membership, silo);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var end = start.AddHours(1);
        var shard = await manager.CreateShardAsync(
            start,
            end,
            new Dictionary<string, string> { ["Purpose"] = "ConcurrentBatching" },
            cancellationToken);
        var observedBatchSizes = new ConcurrentBag<long>();
        using var listener = CreateStorageBatchSizeListener(observedBatchSizes);

        var scheduleTasks = Enumerable.Range(0, JobCount)
            .Select(index => shard.TryScheduleJobAsync(new()
            {
                Target = GrainId.Create("type", $"target-{index}"),
                JobName = $"batched-job-{index}",
                DueTime = start.AddSeconds(1),
                Metadata = null
            }, cancellationToken))
            .ToArray();

        await storageProvider.AppendStarted.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        storageProvider.AllowAppends();
        var scheduledJobs = await Task.WhenAll(scheduleTasks).WaitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);

        Assert.All(scheduledJobs, job => Assert.NotNull(job));
        Assert.True(
            storageProvider.AppendCount < JobCount,
            $"Expected fewer storage appends than scheduled jobs, but saw {storageProvider.AppendCount} appends for {JobCount} jobs.");
        Assert.Contains(observedBatchSizes, size => size > 1);

        await manager.UnregisterShardAsync(shard, cancellationToken);

        var reopenedManager = CreateManager(services, membership, silo);
        var reopened = await reopenedManager.AssignJobShardsAsync(end, int.MaxValue, cancellationToken);
        var reopenedShard = Assert.Single(reopened);
        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in reopenedShard.ConsumeDurableJobsAsync().WithCancellation(cancellationToken))
        {
            consumed.Add(jobContext);
            await reopenedShard.RemoveJobAsync(jobContext.Job.Id, cancellationToken);
        }

        Assert.Equal(JobCount, consumed.Count);
        Assert.Subset(
            consumed.Select(context => context.Job.Id).ToHashSet(StringComparer.Ordinal),
            scheduledJobs.Select(job => job!.Id).ToHashSet(StringComparer.Ordinal));

        await reopenedManager.UnregisterShardAsync(reopenedShard, cancellationToken);
    }

    [Fact]
    public async Task ConcurrentSchedules_WhenOneRequestIsInvalid_PersistsOtherRequests()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const int JobCount = 8;
        const int InvalidIndex = 3;
        var storageProvider = new CountingJournalStorageProvider(delayAppends: true);
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5017), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);
        var manager = CreateManager(services, membership, silo);
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var end = start.AddHours(1);
        var shard = await manager.CreateShardAsync(
            start,
            end,
            new Dictionary<string, string> { ["Purpose"] = "ConcurrentBatchingInvalidItem" },
            cancellationToken);

        var scheduleTasks = Enumerable.Range(0, JobCount)
            .Select(index => shard.TryScheduleJobAsync(new()
            {
                Target = GrainId.Create("type", $"target-{index}"),
                JobName = $"batched-job-{index}",
                DueTime = index == InvalidIndex ? end.AddSeconds(1) : start.AddSeconds(1),
                Metadata = null
            }, cancellationToken))
            .ToArray();

        await storageProvider.AppendStarted.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        storageProvider.AllowAppends();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scheduleTasks[InvalidIndex].WaitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken));
        var scheduledJobs = await Task.WhenAll(scheduleTasks.Where((_, index) => index != InvalidIndex)).WaitAsync(
            TimeSpan.FromSeconds(5),
            cancellationToken);

        Assert.All(scheduledJobs, job => Assert.NotNull(job));
        Assert.True(
            storageProvider.AppendCount < JobCount,
            $"Expected fewer storage appends than scheduled jobs, but saw {storageProvider.AppendCount} appends for {JobCount - 1} valid jobs.");

        await manager.UnregisterShardAsync(shard, cancellationToken);

        var reopenedManager = CreateManager(services, membership, silo);
        var reopened = await reopenedManager.AssignJobShardsAsync(end, int.MaxValue, cancellationToken);
        var reopenedShard = Assert.Single(reopened);
        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in reopenedShard.ConsumeDurableJobsAsync().WithCancellation(cancellationToken))
        {
            consumed.Add(jobContext);
            await reopenedShard.RemoveJobAsync(jobContext.Job.Id, cancellationToken);
        }

        Assert.Equal(JobCount - 1, consumed.Count);
        await reopenedManager.UnregisterShardAsync(reopenedShard, cancellationToken);
    }

    [Fact]
    public async Task AttemptReservation_WaitsForPrecedingRemovalToPersist()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new CountingJournalStorageProvider(delayAppends: false);
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5018), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);
        var manager = CreateManager(
            services,
            membership,
            silo,
            new DurableJobsOptions { ShardBatchLingerDelay = TimeSpan.FromMilliseconds(100) });
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var shard = await manager.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "AttemptReservationPersistence" },
            cancellationToken);
        var scheduled = await ScheduleJobAsync(shard, "attempt-reservation-persistence", cancellationToken);
        await using var enumerator = shard.ConsumeDurableJobsAsync().GetAsyncEnumerator(cancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        var jobContext = enumerator.Current;

        storageProvider.BlockAppends();
        var removeTask = shard.RemoveJobAsync(scheduled.Id, cancellationToken);
        var startAttemptTask = shard.TryStartAttemptAsync(jobContext, cancellationToken);

        await storageProvider.AppendStarted.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        Assert.False(startAttemptTask.IsCompleted);

        storageProvider.AllowAppends();
        Assert.Equal(DurableJobMutationResult.Applied, await removeTask);
        Assert.Equal(DurableJobMutationResult.JobNotFound, await startAttemptTask);

        await manager.UnregisterShardAsync(shard, cancellationToken);
    }

    [Fact]
    public async Task DeadOwnerShard_IsAdoptedClosedAndReplayedFromJournal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5020), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5021), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager1.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "DeadOwnerAdoption" },
            cancellationToken);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "dead-owner-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = new Dictionary<string, string> { ["Kind"] = "Adopted" }
        }, cancellationToken);
        Assert.NotNull(scheduled);

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        var claimed = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken);
        var claimedShard = Assert.Single(claimed);
        Assert.True(claimedShard.IsAddingCompleted);
        Assert.Equal("DeadOwnerAdoption", claimedShard.Metadata!["Purpose"]);
        Assert.Equal(silo2, await manager2.GetShardOwnerAsync(claimedShard.Id, cancellationToken));

        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in claimedShard.ConsumeDurableJobsAsync().WithCancellation(cancellationToken))
        {
            consumed.Add(jobContext);
            await claimedShard.RemoveJobAsync(jobContext.Job.Id, cancellationToken);
        }

        var replayed = Assert.Single(consumed);
        Assert.Equal(scheduled.Id, replayed.Job.Id);
        Assert.Equal("Adopted", replayed.Job.Metadata!["Kind"]);
        Assert.Equal(1, replayed.DequeueCount);

        await manager2.UnregisterShardAsync(claimedShard, cancellationToken);
        await shard.DisposeAsync();
    }

    [Fact]
    public async Task DeadOwnerShard_IsPoisonedAfterAdoptionLimitExceeded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5030), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5031), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2, new DurableJobsOptions { MaxAdoptedCount = 0 });
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager1.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "PoisonedShard" },
            cancellationToken);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "poisoned-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, cancellationToken);
        Assert.NotNull(scheduled);

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken));
        Assert.Null(await manager2.GetShardOwnerAsync(shard.Id, cancellationToken));
        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken));

        await shard.DisposeAsync();
    }

    [Fact]
    public async Task LiveLocalShard_IsReturnedAndRemainsWritable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5040), 0);
        membership.SetSiloStatus(silo, SiloStatus.Active);

        var manager = CreateManager(services, membership, silo);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "LiveShard" },
            cancellationToken);

        await ScheduleJobAsync(shard, "first-live-job", cancellationToken);

        var assigned = await manager.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken);
        var assignedShard = Assert.Single(assigned);
        Assert.Equal(shard.Id, assignedShard.Id);
        Assert.False(assignedShard.IsAddingCompleted);
        Assert.Equal("LiveShard", assignedShard.Metadata!["Purpose"]);

        await ScheduleJobAsync(assignedShard, "second-live-job", cancellationToken);
        Assert.Equal(2, await assignedShard.GetJobCountAsync());

        await DrainAndUnregisterAsync(manager, assignedShard, cancellationToken, expectedJobs: 2);
    }

    [Fact]
    public async Task ActiveRemoteOwnerShard_IsNotClaimedUntilOwnerDies()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5050), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5051), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager1.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string> { ["Purpose"] = "ActiveOwner" },
            cancellationToken);
        await ScheduleJobAsync(shard, "active-owner-job", cancellationToken);

        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken));
        Assert.Equal(silo1, await manager2.GetShardOwnerAsync(shard.Id, cancellationToken));

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        var adopted = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken);
        var adoptedShard = Assert.Single(adopted);
        Assert.True(adoptedShard.IsAddingCompleted);
        Assert.Equal("ActiveOwner", adoptedShard.Metadata!["Purpose"]);
        Assert.Equal(silo2, await manager2.GetShardOwnerAsync(adoptedShard.Id, cancellationToken));

        await DrainAndUnregisterAsync(manager2, adoptedShard, cancellationToken);
        await shard.DisposeAsync();
    }

    [Fact]
    public async Task ShardMetadata_RoundTripsKeysRequiringEncoding()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5060), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5061), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        var shard = await manager1.CreateShardAsync(
            start,
            start.AddHours(1),
            new Dictionary<string, string>
            {
                ["key/with/slashes"] = "slash-value",
                ["key+with=base64"] = "base64-value"
            },
            cancellationToken);
        await ScheduleJobAsync(shard, "metadata-job", cancellationToken);
        await manager1.UnregisterShardAsync(shard, cancellationToken);

        var assigned = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken);
        var assignedShard = Assert.Single(assigned);
        Assert.Equal("slash-value", assignedShard.Metadata!["key/with/slashes"]);
        Assert.Equal("base64-value", assignedShard.Metadata["key+with=base64"]);

        await DrainAndUnregisterAsync(manager2, assignedShard, cancellationToken);
    }

    [Fact]
    public async Task SlowStart_LimitsOrphanedShardClaims()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var storageProvider = new VolatileJournalStorageProvider();
        using var services = CreateServices(storageProvider);
        var membership = new TestClusterMembershipService();
        var silo1 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5070), 0);
        var silo2 = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5071), 0);
        membership.SetSiloStatus(silo1, SiloStatus.Active);
        membership.SetSiloStatus(silo2, SiloStatus.Active);

        var manager1 = CreateManager(services, membership, silo1);
        var manager2 = CreateManager(services, membership, silo2);
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);
        for (var i = 0; i < 3; i++)
        {
            var shard = await manager1.CreateShardAsync(
                start,
                start.AddHours(1),
                new Dictionary<string, string> { ["Index"] = i.ToString() },
                cancellationToken);
            await ScheduleJobAsync(shard, $"orphaned-job-{i}", cancellationToken);
            await manager1.UnregisterShardAsync(shard, cancellationToken);
        }

        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), maxNewClaims: 0, cancellationToken));

        var firstClaim = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), maxNewClaims: 1, cancellationToken);
        var firstShard = Assert.Single(firstClaim);
        await DrainAndUnregisterAsync(manager2, firstShard, cancellationToken);

        var remainingClaims = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, cancellationToken);
        Assert.Equal(2, remainingClaims.Count);
        foreach (var shard in remainingClaims)
        {
            await DrainAndUnregisterAsync(manager2, shard, cancellationToken);
        }
    }

    private static ServiceProvider CreateServices(IJournalStorageProvider storageProvider, TimeProvider? timeProvider = null, IJournalStorageCatalog? catalog = null)
    {
        var builder = new TestSiloBuilder();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.AddLogging();
        builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        builder.Services.AddSingleton<IJournalStorageProvider>(storageProvider);
        builder.Services.AddSingleton(catalog ?? (IJournalStorageCatalog)storageProvider);
        return builder.Services.BuildServiceProvider();
    }

    private static MeterListener CreateStorageBatchSizeListener(ConcurrentBag<long> observedBatchSizes)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "Microsoft.Orleans" && instrument.Name == "orleans-durablejobs-storage-batch-size")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) => observedBatchSizes.Add(measurement));
        listener.Start();
        return listener;
    }

    private sealed class CountingJournalStorageProvider : IJournalStorageProvider, IJournalStorageCatalog
    {
        public ConcurrentQueue<JournalId> MetadataReads { get; } = new();
        public Func<JournalId, CancellationToken, ValueTask>? BeforeMetadataRead { get; set; }
        private readonly VolatileJournalStorageProvider _inner = new();
        private readonly Func<CancellationToken, ValueTask>? _onAppend;
        private readonly object _appendGate = new();
        private bool _delayAppends;
        private TaskCompletionSource _appendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _allowAppends = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _appendCount;

        public CountingJournalStorageProvider(bool delayAppends, Func<CancellationToken, ValueTask>? onAppend = null)
        {
            _delayAppends = delayAppends;
            _onAppend = onAppend;
        }

        public Task AppendStarted
        {
            get
            {
                lock (_appendGate)
                {
                    return _appendStarted.Task;
                }
            }
        }

        public int AppendCount => Volatile.Read(ref _appendCount);

        public void BlockAppends()
        {
            lock (_appendGate)
            {
                _appendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _allowAppends = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _delayAppends = true;
            }
        }

        public void AllowAppends()
        {
            lock (_appendGate)
            {
                _delayAppends = false;
                _allowAppends.TrySetResult();
            }
        }

        public IJournalStorage CreateStorage(JournalId journalId) => new CountingJournalStorage(this, journalId, _inner.CreateStorage(journalId));

        public IAsyncEnumerable<JournalId> ListAsync(ListOptions? options = null, CancellationToken cancellationToken = default)
            => _inner.ListAsync(options, cancellationToken);

        private async ValueTask OnAppendAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _appendCount);
            Task? waitTask;
            lock (_appendGate)
            {
                _appendStarted.TrySetResult();
                waitTask = _delayAppends ? _allowAppends.Task : null;
            }

            if (waitTask is not null)
            {
                await waitTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_onAppend is { } onAppend)
            {
                await onAppend(cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class CountingJournalStorage(CountingJournalStorageProvider owner, JournalId journalId, IJournalStorage inner) : IJournalStorage
        {
            public bool IsCompactionRequested => inner.IsCompactionRequested;

            public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default)
                => inner.CreateIfNotExistsAsync(metadata, cancellationToken);

            public async ValueTask<IJournalMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
            {
                owner.MetadataReads.Enqueue(journalId);
                if (owner.BeforeMetadataRead is { } beforeRead)
                {
                    await beforeRead(journalId, cancellationToken);
                }

                return await inner.GetMetadataAsync(cancellationToken);
            }

            public ValueTask<IJournalMetadata?> UpdateMetadataAsync(
                IReadOnlyDictionary<string, string>? set = null,
                IEnumerable<string>? remove = null,
                string? expectedETag = null,
                CancellationToken cancellationToken = default)
                => inner.UpdateMetadataAsync(set, remove, expectedETag, cancellationToken);

            public ValueTask ReadAsync(IJournalStorageConsumer consumer, CancellationToken cancellationToken)
                => inner.ReadAsync(consumer, cancellationToken);

            public async ValueTask AppendAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
            {
                await owner.OnAppendAsync(cancellationToken).ConfigureAwait(false);
                await inner.AppendAsync(value, cancellationToken).ConfigureAwait(false);
            }

            public ValueTask ReplaceAsync(ReadOnlySequence<byte> value, CancellationToken cancellationToken)
                => inner.ReplaceAsync(value, cancellationToken);

            public ValueTask DeleteAsync(CancellationToken cancellationToken)
                => inner.DeleteAsync(cancellationToken);
        }
    }

    private sealed class DiscoveryFixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        private readonly HashSet<IJobShard> _opened = [];

        public DiscoveryFixture()
        {
            Catalog = new ScriptedCatalog(Storage);
            _services = CreateServices(Storage, catalog: Catalog);
            Membership.SetSiloStatus(Silo, SiloStatus.Active);
            Manager = CreateManager(_services, Membership, Silo);
        }

        public DateTimeOffset Now { get; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Horizon => Now.AddHours(1);
        public SiloAddress Silo { get; } = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5100), 0);
        public CountingJournalStorageProvider Storage { get; } = new(delayAppends: false);
        public ScriptedCatalog Catalog { get; }
        public TestClusterMembershipService Membership { get; } = new();
        public JournaledJobShardManager Manager { get; }

        public async Task<JournalId> AddShardAsync(string name, DateTimeOffset start, SiloAddress? owner = null, bool poisoned = false)
        {
            var id = new JobShardId(name).ToJournalId();
            var properties = new Dictionary<string, string>
            {
                ["DurableJobsMinDueTime"] = start.ToString("O"),
                ["DurableJobsMaxDueTime"] = start.AddHours(1).ToString("O"),
                ["DurableJobsPoisoned"] = poisoned.ToString(),
                ["DurableJobsClosed"] = bool.TrueString
            };
            if (owner is not null)
            {
                properties["DurableJobsOwner"] = owner.ToParsableString();
            }

            await Storage.CreateStorage(id).CreateIfNotExistsAsync(properties, TestContext.Current.CancellationToken);
            return id;
        }

        public Task<List<IJobShard>> DiscoverAsync(int maxNewClaims = int.MaxValue, DateTimeOffset? horizon = null)
            => DiscoverWithCancellationAsync(TestContext.Current.CancellationToken, maxNewClaims, horizon);

        public async Task<List<IJobShard>> DiscoverWithCancellationAsync(CancellationToken cancellationToken, int maxNewClaims = int.MaxValue, DateTimeOffset? horizon = null)
        {
            var result = await Manager.DiscoverJobShardsAsync(horizon ?? Horizon, maxNewClaims, cancellationToken);
            _opened.UnionWith(result);
            return result;
        }

        public async Task<List<IJobShard>> AssignAsync()
        {
            var result = await Manager.AssignJobShardsAsync(Horizon, 0, TestContext.Current.CancellationToken);
            _opened.UnionWith(result);
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var shard in _opened)
            {
                await shard.DisposeAsync();
            }

            await _services.DisposeAsync();
        }
    }

    private sealed class ScriptedCatalog(IJournalStorageCatalog legacy) : IJournalStorageCatalog, IPagedJournalStorageCatalog
    {
        public Func<string?, JournalStorageCatalogPage> ReadPage { get; set; } = _ => throw new InvalidOperationException("Configure the catalog pages.");
        public List<string?> Tokens { get; } = [];
        public int ListCalls { get; private set; }

        public ValueTask<JournalStorageCatalogPage> ReadPageAsync(
            JournalId prefix, int pageSize, string? continuationToken = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(JobShardId.StoragePrefix, prefix);
            Assert.Equal(JournaledJobShardManager.CatalogPageSize, pageSize);
            Tokens.Add(continuationToken);
            return ValueTask.FromResult(ReadPage(continuationToken));
        }

        public IAsyncEnumerable<JournalId> ListAsync(JournalId prefix = default, CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return legacy.ListAsync(prefix, cancellationToken);
        }
    }

    private static JournaledJobShardManager CreateManager(
        IServiceProvider services,
        TestClusterMembershipService membership,
        SiloAddress siloAddress,
        DurableJobsOptions? options = null)
        => new(
            new TestLocalSiloDetails(siloAddress),
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            Options.Create(options ?? new DurableJobsOptions()),
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());

    private static async Task<DurableJob> ScheduleJobAsync(IJobShard shard, string jobName, CancellationToken cancellationToken)
    {
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", jobName),
            JobName = jobName,
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, cancellationToken);
        Assert.NotNull(scheduled);
        return scheduled;
    }

    private static async Task DrainAndUnregisterAsync(
        JournaledJobShardManager manager,
        IJobShard shard,
        CancellationToken cancellationToken,
        int expectedJobs = 1)
    {
        var consumed = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        await foreach (var jobContext in shard.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumed++;
            Assert.Equal(
                DurableJobMutationResult.Applied,
                await shard.RemoveJobAsync(jobContext.Job.Id, cts.Token));
            if (consumed == expectedJobs)
            {
                break;
            }
        }

        Assert.Equal(expectedJobs, consumed);
        Assert.Equal(0, await shard.GetJobCountAsync());
        await manager.UnregisterShardAsync(shard, cancellationToken);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        var result = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }

    private sealed class TestLocalSiloDetails(SiloAddress siloAddress) : ILocalSiloDetails
    {
        public string Name => SiloAddress.ToParsableString();

        public string ClusterId => "TestCluster";

        public string DnsHostName => SiloAddress.ToParsableString();

        public SiloAddress SiloAddress { get; } = siloAddress;

        public SiloAddress GatewayAddress => SiloAddress;
    }

    private sealed class TestClusterMembershipService : IClusterMembershipService
    {
        private ImmutableDictionary<SiloAddress, ClusterMember> _members = ImmutableDictionary<SiloAddress, ClusterMember>.Empty;
        private long _version;

        public ClusterMembershipSnapshot CurrentSnapshot => new(_members, new MembershipVersion(_version));

        public IAsyncEnumerable<ClusterMembershipSnapshot> MembershipUpdates => GetMembershipUpdates();

        public void SetSiloStatus(SiloAddress siloAddress, SiloStatus status)
        {
            _members = _members.SetItem(siloAddress, new ClusterMember(siloAddress, status, siloAddress.ToParsableString()));
            _version++;
        }

        public ValueTask Refresh(MembershipVersion minimumVersion = default, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public Task<bool> TryKill(SiloAddress siloAddress) => Task.FromResult(false);

        private static async IAsyncEnumerable<ClusterMembershipSnapshot> GetMembershipUpdates()
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}

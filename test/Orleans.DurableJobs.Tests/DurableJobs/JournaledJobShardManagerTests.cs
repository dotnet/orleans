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

[TestCategory("BVT"), TestCategory("DurableJobs")]
public class JournaledJobShardManagerTests
{
    [Fact]
    public async Task ReleasedShard_IsClaimedClosedAndReplayedFromJournal()
    {
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
            CancellationToken.None);

        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = new Dictionary<string, string> { ["Kind"] = "Replay" }
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        await manager1.UnregisterShardAsync(shard, CancellationToken.None);

        var claimed = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        var claimedShard = Assert.Single(claimed);
        Assert.True(claimedShard.IsAddingCompleted);
        Assert.Equal("JournaledManagerTest", claimedShard.Metadata!["Purpose"]);

        var rejected = await claimedShard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target2"),
            JobName = "new-job",
            DueTime = DateTimeOffset.UtcNow,
            Metadata = null
        }, CancellationToken.None);
        Assert.Null(rejected);

        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in claimedShard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            consumed.Add(jobContext);
            await claimedShard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None);
        }

        var replayed = Assert.Single(consumed);
        Assert.Equal(scheduled.Id, replayed.Job.Id);
        Assert.Equal("Replay", replayed.Job.Metadata!["Kind"]);
        Assert.Equal(1, replayed.DequeueCount);

        await manager2.UnregisterShardAsync(claimedShard, CancellationToken.None);
        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyShard_IsDeletedWhenUnregistered()
    {
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
            CancellationToken.None);
        var storageId = ((JournaledJobShard)shard).StorageId;

        Assert.NotNull(await storageProvider.CreateStorage(storageId).GetMetadataAsync());

        await manager.UnregisterShardAsync(shard, CancellationToken.None);

        Assert.Null(await storageProvider.CreateStorage(storageId).GetMetadataAsync());
        Assert.Empty(await ToListAsync(storageProvider.ListAsync(JobShardId.StoragePrefix)));
    }

    [Fact]
    public async Task ClosedLocalShard_CanStillPersistRemovals()
    {
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
            CancellationToken.None);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "closed-local-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        await shard.MarkAsCompleteAsync(CancellationToken.None);
        await manager.UnregisterShardAsync(shard, CancellationToken.None);

        var reopenedManager = CreateManager(services, membership, silo);
        var reopened = await reopenedManager.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        shard = Assert.Single(reopened);
        Assert.True(shard.IsAddingCompleted);

        var rejected = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target2"),
            JobName = "rejected-job",
            DueTime = DateTimeOffset.UtcNow,
            Metadata = null
        }, CancellationToken.None);
        Assert.Null(rejected);

        await foreach (var jobContext in shard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            Assert.Equal(scheduled.Id, jobContext.Job.Id);
            Assert.True(await shard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None));
        }

        Assert.Equal(0, await shard.GetJobCountAsync());
        await reopenedManager.UnregisterShardAsync(shard, CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentSchedules_ArePersistedInStorageBatches()
    {
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
            CancellationToken.None);
        var observedBatchSizes = new ConcurrentBag<long>();
        using var listener = CreateStorageBatchSizeListener(observedBatchSizes);

        var scheduleTasks = Enumerable.Range(0, JobCount)
            .Select(index => shard.TryScheduleJobAsync(new()
            {
                Target = GrainId.Create("type", $"target-{index}"),
                JobName = $"batched-job-{index}",
                DueTime = start.AddSeconds(1),
                Metadata = null
            }, CancellationToken.None))
            .ToArray();

        await storageProvider.AppendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        storageProvider.AllowAppends();
        var scheduledJobs = await Task.WhenAll(scheduleTasks).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(scheduledJobs, job => Assert.NotNull(job));
        Assert.True(
            storageProvider.AppendCount < JobCount,
            $"Expected fewer storage appends than scheduled jobs, but saw {storageProvider.AppendCount} appends for {JobCount} jobs.");
        Assert.Contains(observedBatchSizes, size => size > 1);

        await manager.UnregisterShardAsync(shard, CancellationToken.None);

        var reopenedManager = CreateManager(services, membership, silo);
        var reopened = await reopenedManager.AssignJobShardsAsync(end, int.MaxValue, CancellationToken.None);
        var reopenedShard = Assert.Single(reopened);
        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in reopenedShard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            consumed.Add(jobContext);
            await reopenedShard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None);
        }

        Assert.Equal(JobCount, consumed.Count);
        Assert.Subset(
            consumed.Select(context => context.Job.Id).ToHashSet(StringComparer.Ordinal),
            scheduledJobs.Select(job => job!.Id).ToHashSet(StringComparer.Ordinal));

        await reopenedManager.UnregisterShardAsync(reopenedShard, CancellationToken.None);
    }

    [Fact]
    public async Task ConcurrentSchedules_WhenOneRequestIsInvalid_PersistsOtherRequests()
    {
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
            CancellationToken.None);

        var scheduleTasks = Enumerable.Range(0, JobCount)
            .Select(index => shard.TryScheduleJobAsync(new()
            {
                Target = GrainId.Create("type", $"target-{index}"),
                JobName = $"batched-job-{index}",
                DueTime = index == InvalidIndex ? end.AddSeconds(1) : start.AddSeconds(1),
                Metadata = null
            }, CancellationToken.None))
            .ToArray();

        await storageProvider.AppendStarted.WaitAsync(TimeSpan.FromSeconds(5));
        storageProvider.AllowAppends();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => scheduleTasks[InvalidIndex].WaitAsync(TimeSpan.FromSeconds(5)));
        var scheduledJobs = await Task.WhenAll(scheduleTasks.Where((_, index) => index != InvalidIndex)).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.All(scheduledJobs, job => Assert.NotNull(job));
        Assert.True(
            storageProvider.AppendCount < JobCount,
            $"Expected fewer storage appends than scheduled jobs, but saw {storageProvider.AppendCount} appends for {JobCount - 1} valid jobs.");

        await manager.UnregisterShardAsync(shard, CancellationToken.None);

        var reopenedManager = CreateManager(services, membership, silo);
        var reopened = await reopenedManager.AssignJobShardsAsync(end, int.MaxValue, CancellationToken.None);
        var reopenedShard = Assert.Single(reopened);
        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in reopenedShard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            consumed.Add(jobContext);
            await reopenedShard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None);
        }

        Assert.Equal(JobCount - 1, consumed.Count);
        await reopenedManager.UnregisterShardAsync(reopenedShard, CancellationToken.None);
    }

    [Fact]
    public async Task DeadOwnerShard_IsAdoptedClosedAndReplayedFromJournal()
    {
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
            CancellationToken.None);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "dead-owner-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = new Dictionary<string, string> { ["Kind"] = "Adopted" }
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        var claimed = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        var claimedShard = Assert.Single(claimed);
        Assert.True(claimedShard.IsAddingCompleted);
        Assert.Equal("DeadOwnerAdoption", claimedShard.Metadata!["Purpose"]);
        Assert.Equal(silo2, await manager2.GetShardOwnerAsync(claimedShard.Id, CancellationToken.None));

        var consumed = new List<IJobRunContext>();
        await foreach (var jobContext in claimedShard.ConsumeDurableJobsAsync().WithCancellation(CancellationToken.None))
        {
            consumed.Add(jobContext);
            await claimedShard.RemoveJobAsync(jobContext.Job.Id, CancellationToken.None);
        }

        var replayed = Assert.Single(consumed);
        Assert.Equal(scheduled.Id, replayed.Job.Id);
        Assert.Equal("Adopted", replayed.Job.Metadata!["Kind"]);
        Assert.Equal(1, replayed.DequeueCount);

        await manager2.UnregisterShardAsync(claimedShard, CancellationToken.None);
        await shard.DisposeAsync();
    }

    [Fact]
    public async Task DeadOwnerShard_IsPoisonedAfterAdoptionLimitExceeded()
    {
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
            CancellationToken.None);
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", "target"),
            JobName = "poisoned-job",
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, CancellationToken.None);
        Assert.NotNull(scheduled);

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None));
        Assert.Null(await manager2.GetShardOwnerAsync(shard.Id, CancellationToken.None));
        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None));

        await shard.DisposeAsync();
    }

    [Fact]
    public async Task LiveLocalShard_IsReturnedAndRemainsWritable()
    {
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
            CancellationToken.None);

        await ScheduleJobAsync(shard, "first-live-job");

        var assigned = await manager.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        var assignedShard = Assert.Single(assigned);
        Assert.Equal(shard.Id, assignedShard.Id);
        Assert.False(assignedShard.IsAddingCompleted);
        Assert.Equal("LiveShard", assignedShard.Metadata!["Purpose"]);

        await ScheduleJobAsync(assignedShard, "second-live-job");
        Assert.Equal(2, await assignedShard.GetJobCountAsync());

        await DrainAndUnregisterAsync(manager, assignedShard, expectedJobs: 2);
    }

    [Fact]
    public async Task ActiveRemoteOwnerShard_IsNotClaimedUntilOwnerDies()
    {
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
            CancellationToken.None);
        await ScheduleJobAsync(shard, "active-owner-job");

        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None));
        Assert.Equal(silo1, await manager2.GetShardOwnerAsync(shard.Id, CancellationToken.None));

        membership.SetSiloStatus(silo1, SiloStatus.Dead);

        var adopted = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        var adoptedShard = Assert.Single(adopted);
        Assert.True(adoptedShard.IsAddingCompleted);
        Assert.Equal("ActiveOwner", adoptedShard.Metadata!["Purpose"]);
        Assert.Equal(silo2, await manager2.GetShardOwnerAsync(adoptedShard.Id, CancellationToken.None));

        await DrainAndUnregisterAsync(manager2, adoptedShard);
        await shard.DisposeAsync();
    }

    [Fact]
    public async Task ShardMetadata_RoundTripsKeysRequiringEncoding()
    {
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
            CancellationToken.None);
        await ScheduleJobAsync(shard, "metadata-job");
        await manager1.UnregisterShardAsync(shard, CancellationToken.None);

        var assigned = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        var assignedShard = Assert.Single(assigned);
        Assert.Equal("slash-value", assignedShard.Metadata!["key/with/slashes"]);
        Assert.Equal("base64-value", assignedShard.Metadata["key+with=base64"]);

        await DrainAndUnregisterAsync(manager2, assignedShard);
    }

    [Fact]
    public async Task SlowStart_LimitsOrphanedShardClaims()
    {
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
                CancellationToken.None);
            await ScheduleJobAsync(shard, $"orphaned-job-{i}");
            await manager1.UnregisterShardAsync(shard, CancellationToken.None);
        }

        Assert.Empty(await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), maxNewClaims: 0, CancellationToken.None));

        var firstClaim = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), maxNewClaims: 1, CancellationToken.None);
        var firstShard = Assert.Single(firstClaim);
        await DrainAndUnregisterAsync(manager2, firstShard);

        var remainingClaims = await manager2.AssignJobShardsAsync(DateTimeOffset.UtcNow.AddHours(1), int.MaxValue, CancellationToken.None);
        Assert.Equal(2, remainingClaims.Count);
        foreach (var shard in remainingClaims)
        {
            await DrainAndUnregisterAsync(manager2, shard);
        }
    }

    private static ServiceProvider CreateServices(IJournalStorageProvider storageProvider)
    {
        var builder = new TestSiloBuilder();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.AddLogging();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddKeyedSingleton<TimeProvider>(KeyedService.AnyKey, static (sp, _) => sp.GetRequiredService<TimeProvider>());
        builder.Services.AddSingleton<IJournalStorageProvider>(storageProvider);
        builder.Services.AddSingleton((IJournalStorageCatalog)storageProvider);
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
        private readonly VolatileJournalStorageProvider _inner = new();
        private readonly bool _delayAppends;
        private readonly TaskCompletionSource _appendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowAppends = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _appendCount;

        public CountingJournalStorageProvider(bool delayAppends)
        {
            _delayAppends = delayAppends;
            if (!delayAppends)
            {
                _allowAppends.SetResult();
            }
        }

        public Task AppendStarted => _appendStarted.Task;

        public int AppendCount => Volatile.Read(ref _appendCount);

        public void AllowAppends() => _allowAppends.TrySetResult();

        public IJournalStorage CreateStorage(JournalId journalId) => new CountingJournalStorage(this, _inner.CreateStorage(journalId));

        public IAsyncEnumerable<JournalId> ListAsync(JournalId prefix = default, CancellationToken cancellationToken = default)
            => _inner.ListAsync(prefix, cancellationToken);

        private async ValueTask OnAppendAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _appendCount);
            _appendStarted.TrySetResult();
            if (_delayAppends)
            {
                await _allowAppends.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private sealed class CountingJournalStorage(CountingJournalStorageProvider owner, IJournalStorage inner) : IJournalStorage
        {
            public bool IsCompactionRequested => inner.IsCompactionRequested;

            public ValueTask<bool> CreateIfNotExistsAsync(IReadOnlyDictionary<string, string> metadata = null, CancellationToken cancellationToken = default)
                => inner.CreateIfNotExistsAsync(metadata, cancellationToken);

            public ValueTask<IJournalMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
                => inner.GetMetadataAsync(cancellationToken);

            public ValueTask<IJournalMetadata> UpdateMetadataAsync(
                IReadOnlyDictionary<string, string> set = null,
                IEnumerable<string> remove = null,
                string expectedETag = null,
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

    private static JournaledJobShardManager CreateManager(
        IServiceProvider services,
        TestClusterMembershipService membership,
        SiloAddress siloAddress,
        DurableJobsOptions options = null)
        => new(
            new TestLocalSiloDetails(siloAddress),
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            Options.Create(options ?? new DurableJobsOptions()),
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());

    private static async Task<DurableJob> ScheduleJobAsync(IJobShard shard, string jobName)
    {
        var scheduled = await shard.TryScheduleJobAsync(new()
        {
            Target = GrainId.Create("type", jobName),
            JobName = jobName,
            DueTime = DateTimeOffset.UtcNow.AddSeconds(-1),
            Metadata = null
        }, CancellationToken.None);
        Assert.NotNull(scheduled);
        return scheduled;
    }

    private static async Task DrainAndUnregisterAsync(JournaledJobShardManager manager, IJobShard shard, int expectedJobs = 1)
    {
        var consumed = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await foreach (var jobContext in shard.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            consumed++;
            Assert.True(await shard.RemoveJobAsync(jobContext.Job.Id, cts.Token));
            if (consumed == expectedJobs)
            {
                break;
            }
        }

        Assert.Equal(expectedJobs, consumed);
        Assert.Equal(0, await shard.GetJobCountAsync());
        await manager.UnregisterShardAsync(shard, CancellationToken.None);
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source)
    {
        var result = new List<T>();
        await foreach (var item in source)
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

#nullable enable
#pragma warning disable ORLEANSEXP005

using System.Collections.Immutable;
using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Orleans.Configuration;
using Orleans.DurableJobs;
using Orleans.Hosting;
using Orleans.Journaling;
using Orleans.Journaling.Json;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;
using Xunit;

namespace NonSilo.Tests.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
public class LocalDurableJobManagerTests
{
    [Fact]
    public async Task ProcessShardCheckCycleAsync_MarksExpiredWritableShardComplete()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = CreateSubstituteShard("expired-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.HasWritableShard(shardKey));
        await shard.Received(1).MarkAsCompleteAsync(Arg.Any<CancellationToken>());
        Assert.Equal(timeProvider.GetUtcNow().AddHours(1), shardManager.LastMaxDueTime);
    }

    [Fact]
    public async Task ProcessShardCheckCycleAsync_MarksAllExpiredWritableShardStripesComplete()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardStripeCount = 2;
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var firstShard = CreateSubstituteShard("expired-shard-0", shardKey, shardKey.Add(options.ShardDuration));
        var secondShard = CreateSubstituteShard("expired-shard-1", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, firstShard, stripe: 0);
        accessor.AddWritableShard(shardKey, secondShard, stripe: 1);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.HasWritableShard(shardKey, stripe: 0));
        Assert.False(accessor.HasWritableShard(shardKey, stripe: 1));
        await firstShard.Received(1).MarkAsCompleteAsync(Arg.Any<CancellationToken>());
        await secondShard.Received(1).MarkAsCompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessShardCheckCycleAsync_LeavesNonExpiredWritableShardOpen()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = CreateSubstituteShard("active-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.True(accessor.HasWritableShard(shardKey));
        await shard.DidNotReceive().MarkAsCompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExpiredWritableShard_DrainsThenUnregistersAndDisposes()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = new CompletingShard("draining-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);

        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var runTask));

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, shard.MarkAsCompleteCallCount);
        Assert.Equal(1, shard.DisposeCallCount);
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
    }

    [Fact]
    public async Task AssignedShardActivation_UsesTimeProvider()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardActivationBufferPeriod = TimeSpan.FromMinutes(5);
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shard = new CompletingShard(
            "future-shard",
            timeProvider.GetUtcNow().AddMinutes(10),
            timeProvider.GetUtcNow().AddMinutes(11));
        shardManager.AssignedShards.Add(shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));

        timeProvider.Advance(TimeSpan.FromMinutes(11));

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var runTask));

        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shard.MarkAsCompleteAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CompletedWritableShardCleanup_RemovesWritableShard()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = new CompletingShard("completed-writable-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);

        Assert.True(accessor.TryGetRunningShardTask(shard.Id, out var runTask));

        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await shard.MarkAsCompleteAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(accessor.HasWritableShard(shardKey));
        Assert.False(accessor.HasCachedShard(shard.Id));
        Assert.False(accessor.TryGetRunningShardTask(shard.Id, out _));
        Assert.Same(shard, Assert.Single(shardManager.UnregisteredShards));
        Assert.Equal(1, shard.DisposeCallCount);
    }

    [Fact]
    public async Task CompletedWritableShardCleanup_DoesNotRemoveReplacementWritableShard()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var completedShard = new CompletingShard("completed-writable-shard", shardKey, shardKey.Add(options.ShardDuration));
        var replacementShard = CreateSubstituteShard("replacement-writable-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, completedShard);
        accessor.TryActivateShard(completedShard);

        Assert.True(accessor.TryGetRunningShardTask(completedShard.Id, out var runTask));

        await completedShard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        accessor.AddWritableShard(shardKey, replacementShard);
        await completedShard.MarkAsCompleteAsync(CancellationToken.None);
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(accessor.TryGetWritableShard(shardKey, out var currentShard));
        Assert.Same(replacementShard, currentShard);
    }

    [Fact]
    public async Task ProcessShardCheckCycleAsync_WhenExpiredWritableShardIsDisposed_RemovesWritableShard()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = new DisposedSchedulingShard("disposed-expired-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(accessor.HasWritableShard(shardKey));
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenWritableShardIsDisposed_RemovesStaleShardAndRetries()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var dueTime = timeProvider.GetUtcNow().AddMinutes(5);
        var shardKey = new DateTimeOffset(
            (dueTime.UtcTicks / options.ShardDuration.Ticks) * options.ShardDuration.Ticks,
            TimeSpan.Zero);
        var staleShard = new DisposedSchedulingShard("disposed-writable-shard", shardKey, shardKey.Add(options.ShardDuration));
        var replacementShard = new SchedulingShard("replacement-writable-shard", shardKey, shardKey.Add(options.ShardDuration));
        shardManager.CreateShard = (minDueTime, maxDueTime, _, _) =>
        {
            Assert.Equal(shardKey, minDueTime);
            Assert.Equal(shardKey.Add(options.ShardDuration), maxDueTime);
            return Task.FromResult<IJobShard>(replacementShard);
        };

        accessor.AddWritableShard(shardKey, staleShard);

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "retried-job",
            DueTime = dueTime
        }, CancellationToken.None);

        Assert.Equal("retried-job", job.Name);
        Assert.Equal(replacementShard.Id, job.ShardId);
        Assert.Equal(1, shardManager.CreateShardCallCount);
        Assert.True(accessor.TryGetWritableShard(shardKey, out var currentShard));
        Assert.Same(replacementShard, currentShard);
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenExpiryWaitsBehindScheduling_CompletesShardAfterJobIsAccepted()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow().Subtract(options.ShardDuration * 2);
        var shard = new GateableSchedulingShard("expiring-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);

        var scheduleTask = manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "late-job",
            DueTime = shardKey
        }, CancellationToken.None);

        await shard.ScheduleStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var cycleTask = accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        Assert.False(cycleTask.IsCompleted);

        shard.AllowScheduleToFinish.SetResult();

        var job = await scheduleTask.WaitAsync(TimeSpan.FromSeconds(5));
        await cycleTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("late-job", job.Name);
        Assert.Equal(1, shard.MarkAsCompleteCallCount);
        Assert.True(shard.IsAddingCompleted);
        Assert.False(accessor.HasWritableShard(shardKey));
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenShardStripingEnabled_DistributesJobsAcrossWritableShards()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardStripeCount = 4;
        var shardManager = new TestJobShardManager();
        var manager = CreateManager(shardManager, timeProvider, options);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var dueTime = timeProvider.GetUtcNow().AddMinutes(5);
        var shardKey = new DateTimeOffset(
            (dueTime.UtcTicks / options.ShardDuration.Ticks) * options.ShardDuration.Ticks,
            TimeSpan.Zero);
        var targetsByStripe = new Dictionary<int, GrainId>();
        for (var i = 0; i < 10_000 && targetsByStripe.Count < options.ShardStripeCount; i++)
        {
            var target = GrainId.Create("test", $"target-{i}");
            var request = new ScheduleJobRequest
            {
                Target = target,
                JobName = "striped-job",
                DueTime = dueTime
            };
            targetsByStripe.TryAdd(accessor.GetWritableShardStripe(request), target);
        }

        Assert.Equal(options.ShardStripeCount, targetsByStripe.Count);

        shardManager.CreateShard = (minDueTime, maxDueTime, metadata, _) =>
        {
            Assert.Equal(shardKey, minDueTime);
            Assert.Equal(shardKey.Add(options.ShardDuration), maxDueTime);
            Assert.True(metadata.TryGetValue("stripe", out var stripe));
            return Task.FromResult<IJobShard>(new SchedulingShard($"stripe-shard-{stripe}", minDueTime, maxDueTime));
        };

        var initialJobs = await Task.WhenAll(targetsByStripe
            .OrderBy(static entry => entry.Key)
            .Select(entry => manager.ScheduleJobAsync(new()
            {
                Target = entry.Value,
                JobName = "striped-job",
                DueTime = dueTime
            }, CancellationToken.None)));

        Assert.Equal(options.ShardStripeCount, shardManager.CreateShardCallCount);
        Assert.Equal(options.ShardStripeCount, accessor.WritableShardCount);
        Assert.Equal(options.ShardStripeCount, initialJobs.Select(static job => job.ShardId).Distinct().Count());

        var secondRoundJobs = await Task.WhenAll(targetsByStripe
            .OrderBy(static entry => entry.Key)
            .Select(entry => manager.ScheduleJobAsync(new()
            {
                Target = entry.Value,
                JobName = "striped-job",
                DueTime = dueTime
            }, CancellationToken.None)));

        Assert.Equal(options.ShardStripeCount, shardManager.CreateShardCallCount);
        Assert.Equal(
            initialJobs.Select(static job => job.ShardId).OrderBy(static id => id).ToArray(),
            secondRoundJobs.Select(static job => job.ShardId).OrderBy(static id => id).ToArray());
    }

    [Fact]
    public async Task ExpiredJournaledShard_DrainsUnregistersAndDeletesStorage()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardDuration = TimeSpan.FromSeconds(1);
        var storageProvider = new VolatileJournalStorageProvider();
        await using var services = CreateJournaledServices(storageProvider, timeProvider);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(siloAddress, SiloStatus.Active);
        var optionsWrapper = Options.Create(options);
        var journaledShardManager = new JournaledJobShardManager(
            localSiloDetails,
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            optionsWrapper,
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        var (grainFactory, handledJob) = CreateCompletingGrainFactory();
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);
        var manager = new LocalDurableJobManager(
            journaledShardManager,
            shardExecutor,
            grainFactory,
            membership,
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            NullLogger<LocalDurableJobManager>.Instance);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "journaled-job",
            DueTime = timeProvider.GetUtcNow()
        }, CancellationToken.None);

        Assert.True(accessor.TryGetRunningShardTask(job.ShardId, out var runTask));

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);

        var jobContext = await handledJob.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(job.Id, jobContext.Job.Id);
        await AdvanceUntilCompletedAsync(timeProvider, runTask!, TimeSpan.FromSeconds(1));
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await storageProvider.CreateStorage(JobShardId.Parse(job.ShardId).ToJournalId()).GetMetadataAsync());
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenJobIsDueNow_DispatchesWithoutAdvancingTime()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardDuration = TimeSpan.FromSeconds(1);
        var storageProvider = new VolatileJournalStorageProvider();
        await using var services = CreateJournaledServices(storageProvider, timeProvider);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(siloAddress, SiloStatus.Active);
        var optionsWrapper = Options.Create(options);
        var journaledShardManager = new JournaledJobShardManager(
            localSiloDetails,
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            optionsWrapper,
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        var (grainFactory, handledJob, getHandleCount) = CreateCountingGrainFactory(timeProvider);
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);
        var manager = new LocalDurableJobManager(
            journaledShardManager,
            shardExecutor,
            grainFactory,
            membership,
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            NullLogger<LocalDurableJobManager>.Instance);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "due-now-job",
            DueTime = timeProvider.GetUtcNow()
        }, CancellationToken.None);

        Assert.True(accessor.TryGetRunningShardTask(job.ShardId, out var runTask));
        var jobContext = await handledJob.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(job.Id, jobContext.Job.Id);
        Assert.Equal(1, getHandleCount());

        timeProvider.Advance(TimeSpan.FromSeconds(3));
        await accessor.ProcessShardCheckCycleAsync(CancellationToken.None);
        await AdvanceUntilCompletedAsync(timeProvider, runTask!, TimeSpan.FromSeconds(1));
        await runTask!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, getHandleCount());
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenJobIsDueNow_WaitsForShardConsumer()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        var shardManager = new TestJobShardManager();
        var (grainFactory, handledJob, _) = CreateCountingGrainFactory(timeProvider);
        var manager = CreateManager(shardManager, timeProvider, options, grainFactory);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);
        var shardKey = timeProvider.GetUtcNow();
        var shard = new ControlledConsumingShard("controlled-shard", shardKey, shardKey.Add(options.ShardDuration));

        accessor.AddWritableShard(shardKey, shard);
        accessor.TryActivateShard(shard);
        await shard.ConsumeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "due-now-job",
            DueTime = shardKey
        }, CancellationToken.None);

        var unexpectedDispatch = await Task.WhenAny(handledJob.Task, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(handledJob.Task, unexpectedDispatch);

        shard.AllowConsume.SetResult();

        var jobContext = await handledJob.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(job.Id, jobContext.Job.Id);
    }

    [Fact]
    public async Task ScheduleJobAsync_WhenJobIsDueInFuture_DoesNotDispatchImmediately()
    {
        var timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var options = CreateOptions();
        options.ShardDuration = TimeSpan.FromSeconds(1);
        var storageProvider = new VolatileJournalStorageProvider();
        await using var services = CreateJournaledServices(storageProvider, timeProvider);
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5010), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        var membership = new TestClusterMembershipService();
        membership.SetSiloStatus(siloAddress, SiloStatus.Active);
        var optionsWrapper = Options.Create(options);
        var journaledShardManager = new JournaledJobShardManager(
            localSiloDetails,
            services.GetRequiredService<IJournaledStateManagerFactory>(),
            services.GetRequiredService<IJournalStorageProvider>(),
            services.GetRequiredService<IJournalStorageCatalog>(),
            membership,
            services,
            optionsWrapper,
            services.GetRequiredService<IOptions<JournaledStateManagerOptions>>());
        var (grainFactory, handledJob, getHandleCount) = CreateCountingGrainFactory(timeProvider);
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);
        var manager = new LocalDurableJobManager(
            journaledShardManager,
            shardExecutor,
            grainFactory,
            membership,
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            NullLogger<LocalDurableJobManager>.Instance);
        var accessor = new LocalDurableJobManager.TestAccessor(manager);

        var job = await manager.ScheduleJobAsync(new()
        {
            Target = GrainId.Create("test", "target"),
            JobName = "future-job",
            DueTime = timeProvider.GetUtcNow().AddSeconds(5)
        }, CancellationToken.None);

        Assert.False(handledJob.Task.IsCompleted);
        Assert.Equal(0, getHandleCount());
        Assert.False(accessor.TryGetRunningShardTask(job.ShardId, out _));
    }

    private static DurableJobsOptions CreateOptions() => new()
    {
        ShardDuration = TimeSpan.FromMinutes(1),
        ShardActivationBufferPeriod = TimeSpan.Zero,
        ShardClaimRampUpDuration = TimeSpan.Zero,
        ConcurrencySlowStartEnabled = false,
        MaxConcurrentJobsPerSilo = 10
    };

    private static LocalDurableJobManager CreateManager(
        JobShardManager shardManager,
        FakeTimeProvider timeProvider,
        DurableJobsOptions options,
        IInternalGrainFactory? grainFactory = null)
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        grainFactory ??= Substitute.For<IInternalGrainFactory>();
        var overloadDetector = Substitute.For<IOverloadDetector>();
        overloadDetector.IsOverloaded.Returns(false);
        var optionsWrapper = Options.Create(options);
        var shardExecutor = new ShardExecutor(
            grainFactory,
            optionsWrapper,
            overloadDetector,
            NullLogger<ShardExecutor>.Instance,
            timeProvider);

        return new LocalDurableJobManager(
            shardManager,
            shardExecutor,
            grainFactory,
            new TestClusterMembershipService(),
            overloadDetector,
            timeProvider,
            optionsWrapper,
            CreateSystemTargetShared(localSiloDetails),
            NullLogger<LocalDurableJobManager>.Instance);
    }

    private static SystemTargetShared CreateSystemTargetShared(ILocalSiloDetails localSiloDetails) => new(
        runtimeClient: null!,
        localSiloDetails,
        NullLoggerFactory.Instance,
        Options.Create(new SchedulingOptions()),
        grainReferenceActivator: null!,
        timerRegistry: null!,
        activations: new ActivationDirectory(CreateCatalogInstruments()),
        schedulerInstruments: CreateSchedulerInstruments());

    private static SchedulerInstruments CreateSchedulerInstruments()
    {        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<SchedulerInstruments>();
        return services.BuildServiceProvider().GetRequiredService<SchedulerInstruments>();
    }

    private static CatalogInstruments CreateCatalogInstruments()
    {
        var services = new ServiceCollection();
        services.AddMetrics();
        services.AddSingleton<OrleansInstruments>();
        services.AddSingleton<CatalogInstruments>();
        return services.BuildServiceProvider().GetRequiredService<CatalogInstruments>();
    }

    private static IJobShard CreateSubstituteShard(string id, DateTimeOffset start, DateTimeOffset end)
    {
        var shard = Substitute.For<IJobShard>();
        shard.Id.Returns(id);
        shard.StartTime.Returns(start);
        shard.EndTime.Returns(end);
        shard.MarkAsCompleteAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return shard;
    }

    private static ServiceProvider CreateJournaledServices(VolatileJournalStorageProvider storageProvider, TimeProvider timeProvider)
    {
        var builder = new TestSiloBuilder();
        builder.AddJournalStorage();
        builder.UseJsonJournalFormat(options => options.AddTypeInfoResolver(DurableJobsJsonContext.Default));
        builder.Services.AddLogging();
        builder.Services.AddSingleton(timeProvider);
        builder.Services.AddSingleton<IJournalStorageProvider>(storageProvider);
        builder.Services.AddSingleton<IJournalStorageCatalog>(storageProvider);
        return builder.Services.BuildServiceProvider();
    }

    private static (IInternalGrainFactory GrainFactory, TaskCompletionSource<IJobRunContext> HandledJob) CreateCompletingGrainFactory()
    {
        var handledJob = new TaskCompletionSource<IJobRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        var extension = Substitute.For<IDurableJobReceiverExtension>();
        extension.HandleDurableJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                handledJob.TrySetResult(callInfo.ArgAt<IJobRunContext>(0));
                return DurableJobRunResult.Completed;
            });
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        return (grainFactory, handledJob);
    }

    private static (IInternalGrainFactory GrainFactory, TaskCompletionSource<IJobRunContext> HandledJob, Func<int> GetHandleCount) CreateCountingGrainFactory(TimeProvider timeProvider)
    {
        var handledJob = new TaskCompletionSource<IJobRunContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handleCount = 0;
        var handler = Substitute.For<IDurableJobHandler>();
        handler.ExecuteJobAsync(Arg.Any<IJobRunContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Interlocked.Increment(ref handleCount);
                handledJob.TrySetResult(callInfo.ArgAt<IJobRunContext>(0));
                return Task.CompletedTask;
            });

        var grainContext = Substitute.For<IGrainContext>();
        grainContext.GrainInstance.Returns(handler);
        grainContext.GrainId.Returns(GrainId.Create("test", "target"));
        var shared = new DurableJobReceiverExtensionShared(
            NullLogger<DurableJobReceiverExtension>.Instance,
            Options.Create(new DurableJobsOptions()),
            Options.Create(new SiloMessagingOptions()),
            timeProvider);
        var extension = new DurableJobReceiverExtension(grainContext, shared);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        return (grainFactory, handledJob, () => Volatile.Read(ref handleCount));
    }

    private static async Task AdvanceUntilCompletedAsync(FakeTimeProvider timeProvider, Task task, TimeSpan advanceBy)
    {
        for (var i = 0; i < 10 && !task.IsCompleted; i++)
        {
            await Task.Yield();
            timeProvider.Advance(advanceBy);
        }
    }

    private sealed class CompletingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MarkAsCompleteCallCount;
        public int DisposeCallCount;
        public TaskCompletionSource ConsumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => _completed.Task.IsCompleted;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref MarkAsCompleteCallCount);
            _completed.TrySetResult();
            return Task.CompletedTask;
        }

        public Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) => Task.FromResult<DurableJob?>(null);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCallCount);
            return ValueTask.CompletedTask;
        }

        private async IAsyncEnumerable<IJobRunContext> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ConsumeStarted.TrySetResult();
            await _completed.Task.WaitAsync(cancellationToken);
            yield break;
        }
    }

    private sealed class ControlledConsumingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        private readonly TaskCompletionSource<DurableJob> _scheduledJob = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ConsumeStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowConsume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => false;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(_scheduledJob.Task.IsCompleted ? 1 : 0);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(true);

        public Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            var job = new DurableJob
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = Id,
                Metadata = request.Metadata
            };

            _scheduledJob.SetResult(job);
            return Task.FromResult<DurableJob?>(job);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async IAsyncEnumerable<IJobRunContext> ConsumeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ConsumeStarted.TrySetResult();
            var job = await _scheduledJob.Task.WaitAsync(cancellationToken);
            await AllowConsume.Task.WaitAsync(cancellationToken);
            yield return new JobRunContext(job, Guid.NewGuid().ToString(), retryCount: 1);
        }
    }

    private sealed class DisposedSchedulingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => true;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken) => throw new ObjectDisposedException(GetType().FullName);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<IJobRunContext> ConsumeAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class SchedulingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => false;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public Task MarkAsCompleteAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            return Task.FromResult<DurableJob?>(new()
            {
                Id = Guid.NewGuid().ToString(),
                Name = request.JobName,
                DueTime = request.DueTime,
                TargetGrainId = request.Target,
                ShardId = Id,
                Metadata = request.Metadata
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static async IAsyncEnumerable<IJobRunContext> ConsumeAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class GateableSchedulingShard(string id, DateTimeOffset start, DateTimeOffset end) : IJobShard
    {
        private readonly SemaphoreSlim _lock = new(1, 1);
        private bool _completed;

        public TaskCompletionSource ScheduleStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowScheduleToFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MarkAsCompleteCallCount;

        public string Id { get; } = id;

        public DateTimeOffset StartTime { get; } = start;

        public DateTimeOffset EndTime { get; } = end;

        public IDictionary<string, string>? Metadata => null;

        public bool IsAddingCompleted => _completed;

        public IAsyncEnumerable<IJobRunContext> ConsumeDurableJobsAsync() => ConsumeAsync();

        public ValueTask<int> GetJobCountAsync() => ValueTask.FromResult(0);

        public async Task MarkAsCompleteAsync(CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                Interlocked.Increment(ref MarkAsCompleteCallCount);
                _completed = true;
            }
            finally
            {
                _lock.Release();
            }
        }

        public Task<bool> RemoveJobAsync(string jobId, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task RetryJobLaterAsync(IJobRunContext jobContext, DateTimeOffset newDueTime, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<DurableJob?> TryScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken)
        {
            await _lock.WaitAsync(cancellationToken);
            try
            {
                ScheduleStarted.TrySetResult();
                await AllowScheduleToFinish.Task.WaitAsync(cancellationToken);
                if (_completed)
                {
                    return null;
                }

                return new DurableJob
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = request.JobName,
                    DueTime = request.DueTime,
                    TargetGrainId = request.Target,
                    ShardId = Id,
                    Metadata = request.Metadata
                };
            }
            finally
            {
                _lock.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            _lock.Dispose();
            return ValueTask.CompletedTask;
        }

        private static async IAsyncEnumerable<IJobRunContext> ConsumeAsync()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TestJobShardManager() : JobShardManager(SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0))
    {
        public List<IJobShard> AssignedShards { get; } = [];

        public List<IJobShard> UnregisteredShards { get; } = [];

        public Func<DateTimeOffset, DateTimeOffset, IDictionary<string, string>, CancellationToken, Task<IJobShard>>? CreateShard { get; set; }

        public int CreateShardCallCount { get; private set; }

        public DateTimeOffset LastMaxDueTime { get; private set; }

        public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
        {
            LastMaxDueTime = maxDueTime;
            return Task.FromResult(AssignedShards.ToList());
        }

        public override Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
        {
            CreateShardCallCount++;
            return CreateShard is null
                ? throw new NotSupportedException()
                : CreateShard(minDueTime, maxDueTime, metadata, cancellationToken);
        }

        public override Task UnregisterShardAsync(IJobShard shard, CancellationToken cancellationToken)
        {
            UnregisteredShards.Add(shard);
            return Task.CompletedTask;
        }
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

    private sealed class TestSiloBuilder : ISiloBuilder
    {
        public IServiceCollection Services { get; } = new ServiceCollection();

        public IConfiguration Configuration { get; } = new ConfigurationBuilder().Build();
    }
}

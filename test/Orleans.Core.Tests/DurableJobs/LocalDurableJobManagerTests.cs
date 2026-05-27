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
        DurableJobsOptions options)
    {
        var siloAddress = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5000), 0);
        var localSiloDetails = new TestLocalSiloDetails(siloAddress);
        var grainFactory = Substitute.For<IInternalGrainFactory>();
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
        activations: new ActivationDirectory());

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
                return Task.FromResult(DurableJobRunResult.Completed);
            });
        grainFactory.GetGrain<IDurableJobReceiverExtension>(Arg.Any<GrainId>()).Returns(extension);
        return (grainFactory, handledJob);
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

        public DateTimeOffset LastMaxDueTime { get; private set; }

        public override Task<List<IJobShard>> AssignJobShardsAsync(DateTimeOffset maxDueTime, int maxNewClaims, CancellationToken cancellationToken)
        {
            LastMaxDueTime = maxDueTime;
            return Task.FromResult(AssignedShards.ToList());
        }

        public override Task<IJobShard> CreateShardAsync(DateTimeOffset minDueTime, DateTimeOffset maxDueTime, IDictionary<string, string> metadata, CancellationToken cancellationToken)
            => throw new NotSupportedException();

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

using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Orleans.DurableJobs;
using Orleans.Journaling;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

public partial class JournaledJobShardManagerTests
{
    [Fact]
    public async Task FlushSignal_DrainsQueuedMutationsAndBarriersWithoutAnotherEnqueue()
    {
        await using var test = await SignalTest.CreateAsync();
        var firstWrite = test.GateNextAppend();
        var first = test.Schedule(0);
        await firstWrite.Started.Task.WaitAsync(test.Token);
        var nextWrite = test.GateNextAppend();
        var queued = Enumerable.Range(1, 3).Select(index => test.Schedule(index)).ToArray();
        var close = test.Shard.MarkAsCompleteAsync(test.Token);
        var rejected = test.Schedule(4);
        var followingBarrier = test.Shard.MarkAsCompleteAsync(test.Token);

        Assert.Empty(await test.ReadPersistedJobNamesAsync());
        Assert.False(first.IsCompleted);
        firstWrite.Release.SetResult();
        await nextWrite.Started.Task.WaitAsync(test.Token);
        Assert.NotNull(await first.WaitAsync(test.Token));
        Assert.Equal(["job-0"], await test.ReadPersistedJobNamesAsync());
        Assert.All(queued, task => Assert.False(task.IsCompleted));
        Assert.False(close.IsCompleted);
        Assert.False(rejected.IsCompleted);
        Assert.False(followingBarrier.IsCompleted);

        nextWrite.Release.SetResult();
        Assert.All(await Task.WhenAll(queued).WaitAsync(test.Token), job => Assert.NotNull(job));
        await Task.WhenAll(close, followingBarrier).WaitAsync(test.Token);
        Assert.Null(await rejected.WaitAsync(test.Token));
        Assert.True(test.Shard.IsAddingCompleted);
        Assert.Equal(2, test.Storage.AppendCount);
        Assert.Equal(Enumerable.Range(0, 4).Select(index => $"job-{index}"), await test.ReadPersistedJobNamesAsync());
    }

    [Fact]
    public async Task FlushSignal_DeleteBarrierFollowsPersistedMutations()
    {
        await using var test = await SignalTest.CreateAsync();
        var write = test.GateNextAppend();
        var first = test.Schedule(0);
        await write.Started.Task.WaitAsync(test.Token);
        var nextWrite = test.GateNextAppend();
        var queued = test.Schedule(1);
        var delete = test.Shard.DeleteStateAsync(test.Token).AsTask();

        write.Release.SetResult();
        await nextWrite.Started.Task.WaitAsync(test.Token);
        Assert.NotNull(await first.WaitAsync(test.Token));
        Assert.False(queued.IsCompleted);
        Assert.False(delete.IsCompleted);
        Assert.Equal(["job-0"], await test.ReadPersistedJobNamesAsync());

        nextWrite.Release.SetResult();
        Assert.NotNull(await queued.WaitAsync(test.Token));
        await delete.WaitAsync(test.Token);
        Assert.Equal(2, test.Storage.AppendCount);
        Assert.Equal(0, await test.Shard.GetJobCountAsync());
        Assert.Null(await test.Storage.CreateStorage(test.Shard.StorageId).GetMetadataAsync(test.Token));
    }

    [Fact]
    public async Task FlushSignal_ConcurrentProducersDrainAfterActiveWrite()
    {
        await using var test = await SignalTest.CreateAsync();
        var write = test.GateNextAppend();
        var first = test.Schedule(0);
        await write.Started.Task.WaitAsync(test.Token);

        var producers = Enumerable.Range(0, 4).Select(producer => Task.Run(() =>
            Enumerable.Range(1 + producer * 8, 8).Select(index => test.Schedule(index)).ToArray())).ToArray();
        var queued = (await Task.WhenAll(producers).WaitAsync(test.Token)).SelectMany(tasks => tasks).ToArray();
        Assert.All(queued, task => Assert.False(task.IsCompleted));
        write.Release.SetResult();

        var jobs = await Task.WhenAll(queued.Prepend(first)).WaitAsync(test.Token);
        Assert.All(jobs, job => Assert.NotNull(job));
        Assert.Equal(33, jobs.Select(job => job!.Id).Distinct().Count());
        Assert.Equal(2, test.Storage.AppendCount);
        Assert.Equal(Enumerable.Range(0, 33).Select(index => $"job-{index}"), await test.ReadPersistedJobNamesAsync());
    }

    [Fact]
    public async Task FlushSignal_CancellationBeforeExecutionPreservesFollowingWork()
    {
        await using var test = await SignalTest.CreateAsync();
        var write = test.GateNextAppend();
        var first = test.Schedule(0);
        await write.Started.Task.WaitAsync(test.Token);
        using var cancellation = new CancellationTokenSource();
        var canceled = test.Schedule(1, cancellation.Token);
        var canceledBarrier = test.Shard.MarkAsCompleteAsync(cancellation.Token);
        var following = test.Schedule(2);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled.WaitAsync(test.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledBarrier.WaitAsync(test.Token));

        write.Release.SetResult();
        Assert.NotNull(await first.WaitAsync(test.Token));
        Assert.NotNull(await following.WaitAsync(test.Token));
        Assert.False(test.Shard.IsAddingCompleted);
        Assert.Equal(2, test.Storage.AppendCount);
        Assert.Equal(["job-0", "job-2"], await test.ReadPersistedJobNamesAsync());
    }

    [Fact]
    public async Task FlushSignal_DisposeIdleShardCompletes()
    {
        await using var test = await SignalTest.CreateAsync();
        await test.Shard.DisposeAsync().AsTask().WaitAsync(test.Token);
        Assert.Equal(0, test.Storage.AppendCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => test.Schedule(0));
    }

    [Fact]
    public async Task FlushSignal_DisposeDuringWriteCancelsActiveAndQueuedWork()
    {
        await using var test = await SignalTest.CreateAsync();
        var write = test.GateNextAppend();
        var active = test.Schedule(0);
        await write.Started.Task.WaitAsync(test.Token);
        var queued = test.Schedule(1);
        var barrier = test.Shard.MarkAsCompleteAsync(test.Token);

        await test.Shard.DisposeAsync().AsTask().WaitAsync(test.Token);
        foreach (var task in new Task[] { active, queued, barrier })
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(test.Token));
        }

        Assert.Equal(1, test.Storage.AppendCount);
        Assert.Empty(await test.ReadPersistedJobNamesAsync());
    }

    [Fact]
    public async Task FlushSignal_EnqueueDuringLingerJoinsPersistedBatch()
    {
        var clock = new SignalTimeProvider();
        await using var test = await SignalTest.CreateAsync(clock);
        var write = test.GateNextAppend();
        var first = test.Schedule(0);
        await clock.TimerCreated.Task.WaitAsync(test.Token);
        var queued = test.Schedule(1);
        clock.Advance(TimeSpan.FromSeconds(1));
        await write.Started.Task.WaitAsync(test.Token);
        Assert.Equal(2, await test.Shard.GetJobCountAsync());
        Assert.False(first.IsCompleted);
        Assert.False(queued.IsCompleted);

        write.Release.SetResult();
        Assert.All(await Task.WhenAll(first, queued).WaitAsync(test.Token), job => Assert.NotNull(job));
        Assert.Equal(1, test.Storage.AppendCount);
        Assert.Equal(["job-0", "job-1"], await test.ReadPersistedJobNamesAsync());
    }

    [Fact]
    public async Task FlushSignal_DisposeDuringLingerCancelsBatchAndBarrier()
    {
        var clock = new SignalTimeProvider();
        await using var test = await SignalTest.CreateAsync(clock);
        var first = test.Schedule(0);
        await clock.TimerCreated.Task.WaitAsync(test.Token);
        var queued = test.Schedule(1);
        var barrier = test.Shard.MarkAsCompleteAsync(test.Token);

        await test.Shard.DisposeAsync().AsTask().WaitAsync(test.Token);
        foreach (var task in new Task[] { first, queued, barrier })
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(test.Token));
        }

        Assert.Equal(0, test.Storage.AppendCount);
        Assert.Empty(await test.ReadPersistedJobNamesAsync());
    }

    private sealed class SignalTimeProvider : FakeTimeProvider
    {
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            TimerCreated.TrySetResult();
            return timer;
        }
    }

    private sealed class AppendGate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class SignalTest : IAsyncDisposable
    {
        private readonly Queue<AppendGate> _writes = new();
        private readonly CancellationTokenSource _timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        private SignalTest(TimeProvider? clock)
        {
            _timeout.CancelAfter(TimeSpan.FromSeconds(30));
            Storage = new CountingJournalStorageProvider(delayAppends: false, onAppend: OnAppendAsync);
            Services = CreateServices(Storage, clock);
        }

        public CancellationToken Token => _timeout.Token;
        public CountingJournalStorageProvider Storage { get; }
        public ServiceProvider Services { get; }
        public JournaledJobShard Shard { get; private set; } = null!;

        public static async Task<SignalTest> CreateAsync(SignalTimeProvider? clock = null)
        {
            var test = new SignalTest(clock);
            var membership = new TestClusterMembershipService();
            var silo = SiloAddress.New(new IPEndPoint(IPAddress.Loopback, 5090), 0);
            membership.SetSiloStatus(silo, SiloStatus.Active);
            var manager = CreateManager(test.Services, membership, silo, new DurableJobsOptions
            {
                ShardBatchLingerDelay = clock is null ? TimeSpan.Zero : TimeSpan.FromSeconds(1)
            });
            var start = (clock?.GetUtcNow() ?? DateTimeOffset.UtcNow).AddMinutes(-1);
            test.Shard = Assert.IsType<JournaledJobShard>(await manager.CreateShardAsync(
                start, start.AddHours(1), new Dictionary<string, string>(), test.Token));
            return test;
        }

        public Task<DurableJob?> Schedule(int index, CancellationToken? cancellationToken = null)
            => Shard.TryScheduleJobAsync(new()
            {
                Target = GrainId.Create("type", "target"),
                JobName = $"job-{index}",
                DueTime = Shard.StartTime.AddSeconds(index + 1)
            }, cancellationToken ?? Token);

        public AppendGate GateNextAppend()
        {
            var gate = new AppendGate();
            lock (_writes)
            {
                _writes.Enqueue(gate);
            }

            return gate;
        }

        public async Task<string[]> ReadPersistedJobNamesAsync()
        {
            var formatKey = Services.GetRequiredService<IOptions<JournaledStateManagerOptions>>().Value.JournalFormatKey;
            var codec = Services.GetRequiredKeyedService<IDurableValueCommandCodec<DurableJobShardJournalRecord>>(formatKey);
            var state = new JournaledJobShardState(JobShardId.Parse(Shard.Id), Shard.StartTime, Shard.EndTime, codec);
            await using var manager = Services.GetRequiredService<IJournaledStateManagerFactory>().Create(Shard.StorageId);
            manager.RegisterState(JournaledJobShardState.StateName, state);
            await manager.InitializeAsync(Token);
            return state.CaptureSnapshot().Jobs.OrderBy(entry => entry.Job.DueTime).Select(entry => entry.Job.Name).ToArray();
        }

        private async ValueTask OnAppendAsync(CancellationToken cancellationToken)
        {
            AppendGate? gate;
            lock (_writes)
            {
                _writes.TryDequeue(out gate);
            }

            if (gate is not null)
            {
                gate.Started.SetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Shard.DisposeAsync();
            await Services.DisposeAsync();
            _timeout.Dispose();
        }
    }
}

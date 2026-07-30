#nullable enable
using Orleans.DurableJobs;
using Orleans.Runtime;
using Xunit;

namespace Tester.DurableJobs;

[TestCategory("BVT"), TestCategory("DurableJobs")]
public sealed class JobShardTests
{
    [Fact]
    public async Task TryScheduleJobAsync_PassesCompleteJobToPersistence()
    {
        var start = DateTimeOffset.UtcNow;
        await using var shard = new RecordingJobShard("shard", start, start.AddHours(1));
        var request = new ScheduleJobRequest
        {
            IdempotencyKey = "generation-1",
            Target = GrainId.Create("test", "persist-complete-job"),
            JobName = "job",
            DueTime = start.AddMinutes(5),
            Priority = 7,
            Metadata = new Dictionary<string, string> { ["key"] = "value" },
            TraceParent = "00-00000000000000000000000000000001-0000000000000001-01",
            TraceState = "vendor=value",
        };

        var result = Assert.IsType<DurableJob>(await shard.TryScheduleJobAsync(request, CancellationToken.None));

        var persisted = Assert.Single(shard.PersistedJobs);
        Assert.Same(result, persisted);
        Assert.Equal(request.Target, persisted.TargetGrainId);
        Assert.Equal(request.JobName, persisted.Name);
        Assert.Equal(request.DueTime, persisted.DueTime);
        Assert.Equal(request.Priority, persisted.Priority);
        Assert.Equal(request.Metadata, persisted.Metadata);
        Assert.NotSame(request.Metadata, persisted.Metadata);
        Assert.Equal(request.TraceParent, persisted.TraceParent);
        Assert.Equal(request.TraceState, persisted.TraceState);
    }

    [Fact]
    public async Task TryScheduleJobAsync_ReusesEquivalentIdempotentRequestWithoutPersistingTwice()
    {
        var start = DateTimeOffset.UtcNow;
        await using var shard = new RecordingJobShard("shard", start, start.AddHours(1));
        var request = new ScheduleJobRequest
        {
            IdempotencyKey = "generation-1",
            Target = GrainId.Create("test", "idempotent-base-shard"),
            JobName = "job",
            DueTime = start.AddMinutes(5),
            Priority = 1,
        };

        var first = Assert.IsType<DurableJob>(await shard.TryScheduleJobAsync(request, CancellationToken.None));
        var second = Assert.IsType<DurableJob>(await shard.TryScheduleJobAsync(request, CancellationToken.None));

        Assert.Same(first, second);
        Assert.Single(shard.PersistedJobs);
    }

    [Fact]
    public async Task TryScheduleJobAsync_RejectsDifferentRequestWithSameIdempotencyKey()
    {
        var start = DateTimeOffset.UtcNow;
        await using var shard = new RecordingJobShard("shard", start, start.AddHours(1));
        var target = GrainId.Create("test", "conflicting-base-shard");
        var request = new ScheduleJobRequest
        {
            IdempotencyKey = "generation-1",
            Target = target,
            JobName = "job",
            DueTime = start.AddMinutes(5),
        };
        _ = await shard.TryScheduleJobAsync(request, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                IdempotencyKey = request.IdempotencyKey,
                Target = target,
                JobName = request.JobName,
                DueTime = request.DueTime,
                Priority = 1,
            },
            CancellationToken.None));

        Assert.Contains("generation-1", exception.Message, StringComparison.Ordinal);
        Assert.Single(shard.PersistedJobs);
    }

    [Fact]
    public async Task TryScheduleJobAsync_SerializesConcurrentConflictingIdempotentRequests()
    {
        var start = DateTimeOffset.UtcNow;
        await using var shard = new RecordingJobShard("shard", start, start.AddHours(1))
        {
            BlockFirstPersistence = true,
        };
        var target = GrainId.Create("test", "concurrent-conflicting-base-shard");
        var request = new ScheduleJobRequest
        {
            IdempotencyKey = "generation-1",
            Target = target,
            JobName = "job",
            DueTime = start.AddMinutes(5),
        };

        var first = shard.TryScheduleJobAsync(request, CancellationToken.None);
        await shard.FirstPersistenceStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var second = shard.TryScheduleJobAsync(
            new ScheduleJobRequest
            {
                IdempotencyKey = request.IdempotencyKey,
                Target = target,
                JobName = request.JobName,
                DueTime = request.DueTime,
                Priority = 1,
            },
            CancellationToken.None);
        shard.ReleaseFirstPersistence();

        _ = Assert.IsType<DurableJob>(await first);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.Contains("generation-1", exception.Message, StringComparison.Ordinal);
        Assert.Single(shard.PersistedJobs);
    }

    private sealed class RecordingJobShard(string id, DateTimeOffset startTime, DateTimeOffset endTime)
        : JobShard(id, startTime, endTime)
    {
        private readonly TaskCompletionSource _firstPersistenceStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstPersistence = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<DurableJob> PersistedJobs { get; } = new();

        public bool BlockFirstPersistence { get; init; }

        public Task FirstPersistenceStarted => _firstPersistenceStarted.Task;

        public void ReleaseFirstPersistence() => _releaseFirstPersistence.TrySetResult();

        protected override async Task PersistAddJobAsync(DurableJob job, CancellationToken cancellationToken)
        {
            PersistedJobs.Add(job);
            if (BlockFirstPersistence && PersistedJobs.Count == 1)
            {
                _firstPersistenceStarted.TrySetResult();
                await _releaseFirstPersistence.Task.WaitAsync(cancellationToken);
            }
        }

        protected override Task PersistRemoveJobAsync(string jobId, CancellationToken cancellationToken)
            => Task.CompletedTask;

        protected override Task PersistRetryJobAsync(string jobId, DateTimeOffset newDueTime, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}

#nullable enable

using TestExtensions;
using Xunit;

namespace Orleans.DurableJobs.Tests;

[TestSuite("BVT")]
[TestCategory("BVT")]
public abstract class JobShardManagerTestsRunner(IJobShardManagerTestFixture fixture)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task TryScheduleJobAsync_InvalidJobName_Throws(string? jobName)
    {
        await using var scope = await fixture.CreateScopeAsync();
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;
        var shard = await manager.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string>(), CancellationToken.None);
        var request = new ScheduleJobRequest
        {
            Target = GrainId.Create("durable-job-test", "target"),
            JobName = jobName!,
            DueTime = now.AddMinutes(1)
        };

        var exception = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => shard.TryScheduleJobAsync(request, CancellationToken.None));

        Assert.Equal("JobName", exception.ParamName);
    }

    [Fact]
    public async Task ShardCreationAndAssignmentUsesDistinctShardIdsForSameWindow()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;

        var shard1 = await manager.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string> { ["index"] = "1" }, cancellationToken);
        var shard2 = await manager.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string> { ["index"] = "2" }, cancellationToken);

        var assigned = await manager.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken);

        Assert.Equal(2, assigned.Count);
        Assert.NotEqual(shard1.Id, shard2.Id);
        Assert.Contains(assigned, shard => shard.Id == shard1.Id && shard.Metadata!["index"] == "1");
        Assert.Contains(assigned, shard => shard.Id == shard2.Id && shard.Metadata!["index"] == "2");
    }

    [Fact]
    public async Task DeadOwnerShardIsReassignedAndPreservesQueuedJobOrderAndMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var newOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await formerOwner.CreateShardAsync(now.AddMinutes(-5), now.AddMinutes(5), Metadata("stream", "alpha"), cancellationToken);
        var later = await ScheduleJobAsync(shard, now.AddSeconds(-1), "later", cancellationToken, Metadata("kind", "later"));
        var earlier = await ScheduleJobAsync(shard, now.AddSeconds(-2), "earlier", cancellationToken, Metadata("kind", "symbols=+/&?"));

        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);

        var assigned = await newOwner.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken);
        var reassigned = Assert.Single(assigned);
        var runs = await TakeAsync(reassigned, 2, cancellationToken);

        Assert.Equal(shard.Id, reassigned.Id);
        Assert.True(reassigned.IsAddingCompleted);
        Assert.Equal([earlier!.Id, later!.Id], runs.Select(run => run.Job.Id).ToArray());
        Assert.Equal("symbols=+/&?", runs[0].Job.Metadata!["kind"]);
        Assert.Equal("later", runs[1].Job.Metadata!["kind"]);
    }

    [Fact]
    public async Task OpenAndClosedShardsAreReassignedAfterFailover()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var newOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var closed = await formerOwner.CreateShardAsync(now, now.AddMinutes(5), Metadata("state", "closed"), cancellationToken);
        await ScheduleJobAsync(closed, now.AddMinutes(-1), "closed-job", cancellationToken);
        await formerOwner.UnregisterShardAsync(closed, cancellationToken);
        var open = await formerOwner.CreateShardAsync(now.AddMinutes(1), now.AddMinutes(6), Metadata("state", "open"), cancellationToken);
        await ScheduleJobAsync(open, now.AddMinutes(1), "open-job", cancellationToken);

        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);

        var assigned = await newOwner.AssignJobShardsAsync(now.AddMinutes(10), int.MaxValue, cancellationToken);

        Assert.Equal(2, assigned.Count);
        Assert.Contains(assigned, shard => shard.Id == open.Id);
        Assert.Contains(assigned, shard => shard.Id == closed.Id);
        Assert.True(assigned.All(static shard => shard.IsAddingCompleted));
    }

    [Fact]
    public async Task LiveShardSchedulesAndConsumesJobsInDueTimeOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;
        var shard = await manager.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), Metadata("stream", "live"), cancellationToken);
        var later = await ScheduleJobAsync(shard, now.AddSeconds(-1), "later", cancellationToken);
        var earlier = await ScheduleJobAsync(shard, now.AddSeconds(-2), "earlier", cancellationToken);

        var assignedShard = Assert.Single(await manager.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken));
        var runs = await TakeAsync(assignedShard, 2, cancellationToken);

        Assert.Equal([earlier!.Id, later!.Id], runs.Select(run => run.Job.Id).ToArray());
    }

    [Fact]
    public async Task ConcurrentOwnershipConflictAllowsOnlyOneManagerToClaimShard()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var creator = scope.CreateManager(scope.ActiveSilo);
        var claimant1 = scope.CreateManager(scope.SecondActiveSilo);
        var claimant2 = scope.CreateManager(scope.ThirdActiveSilo);
        var now = scope.Now;
        var shard = await creator.CreateShardAsync(now, now.AddMinutes(5), Metadata("conflict", "true"), cancellationToken);
        await ScheduleJobAsync(shard, now.AddMinutes(-1), "conflict-job", cancellationToken);
        await creator.UnregisterShardAsync(shard, cancellationToken);

        var claims = await Task.WhenAll(
            claimant1.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken),
            claimant2.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken)).WaitAsync(cancellationToken);

        Assert.Single(claims.SelectMany(static claim => claim));
    }

    [Fact]
    public async Task MetadataIsPreservedAcrossGracefulReassignmentIncludingSpecialCharacters()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var metadata = new Dictionary<string, string>
        {
            ["space key"] = "space value",
            ["symbols-key"] = "symbols=+/&?",
            ["slash/key"] = "slash-value"
        };
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), metadata, cancellationToken);
        await ScheduleJobAsync(shard, now.AddMinutes(-1), "metadata-job", cancellationToken);
        await first.UnregisterShardAsync(shard, cancellationToken);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken));

        Assert.Equal(metadata, reassigned.Metadata);
    }

    [Fact]
    public async Task UnregisterWithJobsRemainingPreservesShardForLaterReassignment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), Metadata("purpose", "stop-processing"), cancellationToken);
        var job = await ScheduleJobAsync(shard, now.AddMinutes(-1), "remaining-job", cancellationToken);

        await first.UnregisterShardAsync(shard, cancellationToken);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken));
        var run = await TakeOneAsync(reassigned, cancellationToken);

        Assert.True(reassigned.IsAddingCompleted);
        Assert.Equal(job!.Id, run.Job.Id);
        Assert.Null(await reassigned.TryScheduleJobAsync(CreateRequest(now.AddMinutes(1), "rejected"), cancellationToken));
    }

    [Fact]
    public async Task AttemptCancellationPreservesJobForReassignment()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), Metadata("purpose", "attempt-cancellation"), CancellationToken.None);
        var job = await ScheduleJobAsync(shard, now.AddMinutes(-1), "attempt-canceled-job");
        var firstAttempt = await TakeOneAsync(shard);

        await first.UnregisterShardAsync(shard, CancellationToken.None);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None));
        var secondAttempt = await TakeOneAsync(reassigned);

        Assert.Equal(job!.Id, secondAttempt.Job.Id);
        Assert.NotEqual(firstAttempt.RunId, secondAttempt.RunId);
        Assert.Equal(1, secondAttempt.DequeueCount);
    }

    [Fact]
    public async Task RetryLaterPersistsThroughShardReassignment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string>(), cancellationToken);
        var job = await ScheduleJobAsync(shard, now.AddMinutes(-1), "retry-job", cancellationToken);
        var run = await TakeOneAsync(shard, cancellationToken);

        await shard.RetryJobLaterAsync(run, now.AddMinutes(-1), cancellationToken);
        await first.UnregisterShardAsync(shard, cancellationToken);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken));
        var retried = await TakeOneAsync(reassigned, cancellationToken);

        Assert.Equal(job!.Id, retried.Job.Id);
        Assert.Equal(run.DequeueCount + 1, retried.DequeueCount);
    }

    [Fact]
    public async Task SuccessfulReschedulePersistsResetAttemptThroughShardReassignment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string>(), cancellationToken);
        var job = await ScheduleJobAsync(shard, now.AddMinutes(-1), "rescheduled-job", cancellationToken);
        var run = await TakeOneAsync(shard, cancellationToken);

        await shard.RescheduleJobAsync(run, now.AddMinutes(-1), cancellationToken);
        await first.UnregisterShardAsync(shard, cancellationToken);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken));
        var rescheduled = await TakeOneAsync(reassigned, cancellationToken);

        Assert.Equal(job!.Id, rescheduled.Job.Id);
        Assert.Equal(1, rescheduled.DequeueCount);
        Assert.NotEqual(run.RunId, rescheduled.RunId);
        Assert.Equal(run.Job.ExecutionGeneration + 1, rescheduled.Job.ExecutionGeneration);
    }

    [Fact]
    public async Task CancellationsBeforeAndDuringProcessingPersistAfterReassignment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now.AddMinutes(-5), now.AddMinutes(5), new Dictionary<string, string>(), cancellationToken);
        var cancelBeforeRun = await ScheduleJobAsync(shard, now.AddMinutes(-3), "cancel-before", cancellationToken);
        var cancelDuringRun = await ScheduleJobAsync(shard, now.AddMinutes(-2), "cancel-during", cancellationToken);
        var remaining = await ScheduleJobAsync(shard, now.AddMinutes(-1), "remaining", cancellationToken);

        Assert.Equal(
            DurableJobMutationResult.Applied,
            await shard.RemoveJobAsync(cancelBeforeRun!.Id, cancellationToken));
        var running = await TakeOneAsync(shard, cancellationToken);
        Assert.Equal(cancelDuringRun!.Id, running.Job.Id);
        Assert.Equal(
            DurableJobMutationResult.Applied,
            await shard.RemoveJobAsync(running.Job.Id, cancellationToken));
        await shard.RetryJobLaterAsync(running, now.AddMinutes(-1), cancellationToken);
        await first.UnregisterShardAsync(shard, cancellationToken);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, cancellationToken));
        var run = await TakeOneAsync(reassigned, cancellationToken);

        Assert.Equal(remaining!.Id, run.Job.Id);
        Assert.Equal(1, await reassigned.GetJobCountAsync());
    }

    [Fact]
    public async Task SlowStartRespectsZeroLimitedUnlimitedAndRepeatedBudgets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var scope = await fixture.CreateScopeAsync(cancellationToken);
        var creator = scope.CreateManager(scope.ActiveSilo);
        var claimant = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var ownedShard = await claimant.CreateShardAsync(now, now.AddMinutes(1), Metadata("owner", "claimant"), cancellationToken);
        var orphanedShardIds = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var shard = await creator.CreateShardAsync(now.AddMinutes(i), now.AddMinutes(i + 1), Metadata("index", i.ToString()), cancellationToken);
            orphanedShardIds.Add(shard.Id);
        }

        scope.SetSiloStatus(scope.ActiveSilo, SiloStatus.Dead);

        var zeroBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), maxNewClaims: 0, cancellationToken);
        var firstLimitedBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), maxNewClaims: 1, cancellationToken);
        var secondLimitedBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), maxNewClaims: 1, cancellationToken);
        var unlimitedBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), int.MaxValue, cancellationToken);

        Assert.Collection(zeroBudget, shard => Assert.Equal(ownedShard.Id, shard.Id));
        Assert.Equal(2, firstLimitedBudget.Count);
        Assert.Equal(3, secondLimitedBudget.Count);
        Assert.Equal(4, unlimitedBudget.Count);
        Assert.Contains(unlimitedBudget, shard => shard.Id == ownedShard.Id);
        Assert.All(orphanedShardIds, id => Assert.Contains(unlimitedBudget, shard => shard.Id == id));
    }

    protected static ScheduleJobRequest CreateRequest(DateTimeOffset dueTime, string jobName, IReadOnlyDictionary<string, string>? metadata = null)
        => new()
        {
            Target = GrainId.Create("durable-job-test", jobName),
            JobName = jobName,
            DueTime = dueTime,
            Metadata = metadata
        };

    private static async Task<DurableJob?> ScheduleJobAsync(
        IJobShard shard,
        DateTimeOffset dueTime,
        string jobName,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (dueTime < shard.StartTime)
        {
            dueTime = shard.StartTime;
        }
        else if (dueTime > shard.EndTime)
        {
            dueTime = shard.EndTime;
        }

        return await shard.TryScheduleJobAsync(CreateRequest(dueTime, jobName, metadata), cancellationToken);
    }

    private static async Task<IJobRunContext> TakeOneAsync(IJobShard shard, CancellationToken cancellationToken)
        => (await TakeAsync(shard, 1, cancellationToken))[0];

    private static async Task<List<IJobRunContext>> TakeAsync(IJobShard shard, int count, CancellationToken cancellationToken)
    {
        var result = new List<IJobRunContext>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        await foreach (var run in shard.ConsumeDurableJobsAsync().WithCancellation(cts.Token))
        {
            result.Add(run);
            if (result.Count == count)
            {
                break;
            }
        }

        Assert.Equal(count, result.Count);
        return result;
    }

    private static Dictionary<string, string> Metadata(string key, string value) => new(StringComparer.Ordinal) { [key] = value };
}

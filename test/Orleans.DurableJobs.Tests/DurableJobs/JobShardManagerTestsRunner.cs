#nullable enable

using TestExtensions;
using Xunit;

namespace Orleans.DurableJobs.Tests;

[TestCategory("BVT")]
public abstract class JobShardManagerTestsRunner(IJobShardManagerTestFixture fixture)
{
    [SkippableFact]
    public async Task ShardCreationAndAssignmentUsesDistinctShardIdsForSameWindow()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;

        var shard1 = await manager.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string> { ["index"] = "1" }, CancellationToken.None);
        var shard2 = await manager.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string> { ["index"] = "2" }, CancellationToken.None);

        var assigned = await manager.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None);

        Assert.Equal(2, assigned.Count);
        Assert.NotEqual(shard1.Id, shard2.Id);
        Assert.Contains(assigned, shard => shard.Id == shard1.Id && shard.Metadata!["index"] == "1");
        Assert.Contains(assigned, shard => shard.Id == shard2.Id && shard.Metadata!["index"] == "2");
    }

    [SkippableFact]
    public async Task DeadOwnerShardIsReassignedAndPreservesQueuedJobOrderAndMetadata()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var newOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await formerOwner.CreateShardAsync(now.AddMinutes(-5), now.AddMinutes(5), Metadata("stream", "alpha"), CancellationToken.None);
        var later = await ScheduleJobAsync(shard, now.AddSeconds(-1), "later", Metadata("kind", "later"));
        var earlier = await ScheduleJobAsync(shard, now.AddSeconds(-2), "earlier", Metadata("kind", "symbols=+/&?"));

        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);

        var assigned = await newOwner.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None);
        var reassigned = Assert.Single(assigned);
        var runs = await TakeAsync(reassigned, 2);

        Assert.Equal(shard.Id, reassigned.Id);
        Assert.True(reassigned.IsAddingCompleted);
        Assert.Equal([earlier!.Id, later!.Id], runs.Select(run => run.Job.Id).ToArray());
        Assert.Equal("symbols=+/&?", runs[0].Job.Metadata!["kind"]);
        Assert.Equal("later", runs[1].Job.Metadata!["kind"]);
    }

    [SkippableFact]
    public async Task OpenAndClosedShardsAreReassignedAfterFailover()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var formerOwner = scope.CreateManager(scope.FormerOwnerSilo);
        var newOwner = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var closed = await formerOwner.CreateShardAsync(now, now.AddMinutes(5), Metadata("state", "closed"), CancellationToken.None);
        await ScheduleJobAsync(closed, now.AddMinutes(-1), "closed-job");
        await formerOwner.UnregisterShardAsync(closed, CancellationToken.None);
        var open = await formerOwner.CreateShardAsync(now.AddMinutes(1), now.AddMinutes(6), Metadata("state", "open"), CancellationToken.None);
        await ScheduleJobAsync(open, now.AddMinutes(1), "open-job");

        scope.SetSiloStatus(scope.FormerOwnerSilo, SiloStatus.Dead);

        var assigned = await newOwner.AssignJobShardsAsync(now.AddMinutes(10), int.MaxValue, CancellationToken.None);

        Assert.Equal(2, assigned.Count);
        Assert.Contains(assigned, shard => shard.Id == open.Id);
        Assert.Contains(assigned, shard => shard.Id == closed.Id);
        Assert.True(assigned.All(static shard => shard.IsAddingCompleted));
    }

    [SkippableFact]
    public async Task LiveShardSchedulesAndConsumesJobsInDueTimeOrder()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var manager = scope.CreateManager(scope.ActiveSilo);
        var now = scope.Now;
        var shard = await manager.CreateShardAsync(now.AddMinutes(-1), now.AddMinutes(5), Metadata("stream", "live"), CancellationToken.None);
        var later = await ScheduleJobAsync(shard, now.AddSeconds(-1), "later");
        var earlier = await ScheduleJobAsync(shard, now.AddSeconds(-2), "earlier");

        var assignedShard = Assert.Single(await manager.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None));
        var runs = await TakeAsync(assignedShard, 2);

        Assert.Equal([earlier!.Id, later!.Id], runs.Select(run => run.Job.Id).ToArray());
    }

    [SkippableFact]
    public async Task ConcurrentOwnershipConflictAllowsOnlyOneManagerToClaimShard()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var creator = scope.CreateManager(scope.ActiveSilo);
        var claimant1 = scope.CreateManager(scope.SecondActiveSilo);
        var claimant2 = scope.CreateManager(scope.ThirdActiveSilo);
        var now = scope.Now;
        var shard = await creator.CreateShardAsync(now, now.AddMinutes(5), Metadata("conflict", "true"), CancellationToken.None);
        await ScheduleJobAsync(shard, now.AddMinutes(-1), "conflict-job");
        await creator.UnregisterShardAsync(shard, CancellationToken.None);

        var claims = await Task.WhenAll(
            claimant1.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None),
            claimant2.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None));

        Assert.Single(claims.SelectMany(static claim => claim));
    }

    [SkippableFact]
    public async Task MetadataIsPreservedAcrossGracefulReassignmentIncludingSpecialCharacters()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var metadata = new Dictionary<string, string>
        {
            ["space key"] = "space value",
            ["symbols-key"] = "symbols=+/&?",
            ["slash/key"] = "slash-value"
        };
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), metadata, CancellationToken.None);
        await ScheduleJobAsync(shard, now.AddMinutes(-1), "metadata-job");
        await first.UnregisterShardAsync(shard, CancellationToken.None);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None));

        Assert.Equal(metadata, reassigned.Metadata);
    }

    [SkippableFact]
    public async Task UnregisterWithJobsRemainingPreservesShardForLaterReassignment()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), Metadata("purpose", "stop-processing"), CancellationToken.None);
        var job = await ScheduleJobAsync(shard, now.AddMinutes(-1), "remaining-job");

        await first.UnregisterShardAsync(shard, CancellationToken.None);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None));
        var run = await TakeOneAsync(reassigned);

        Assert.True(reassigned.IsAddingCompleted);
        Assert.Equal(job!.Id, run.Job.Id);
        Assert.Null(await reassigned.TryScheduleJobAsync(CreateRequest(now.AddMinutes(1), "rejected"), CancellationToken.None));
    }

    [SkippableFact]
    public async Task RetryLaterPersistsThroughShardReassignment()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now, now.AddMinutes(5), new Dictionary<string, string>(), CancellationToken.None);
        var job = await ScheduleJobAsync(shard, now.AddMinutes(-1), "retry-job");
        var run = await TakeOneAsync(shard);

        await shard.RetryJobLaterAsync(run, now.AddMinutes(-1), CancellationToken.None);
        await first.UnregisterShardAsync(shard, CancellationToken.None);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None));
        var retried = await TakeOneAsync(reassigned);

        Assert.Equal(job!.Id, retried.Job.Id);
        Assert.Equal(run.DequeueCount + 1, retried.DequeueCount);
    }

    [SkippableFact]
    public async Task CancellationsBeforeAndDuringProcessingPersistAfterReassignment()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var first = scope.CreateManager(scope.ActiveSilo);
        var second = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var shard = await first.CreateShardAsync(now.AddMinutes(-5), now.AddMinutes(5), new Dictionary<string, string>(), CancellationToken.None);
        var cancelBeforeRun = await ScheduleJobAsync(shard, now.AddMinutes(-3), "cancel-before");
        var cancelDuringRun = await ScheduleJobAsync(shard, now.AddMinutes(-2), "cancel-during");
        var remaining = await ScheduleJobAsync(shard, now.AddMinutes(-1), "remaining");

        Assert.True(await shard.RemoveJobAsync(cancelBeforeRun!.Id, CancellationToken.None));
        var running = await TakeOneAsync(shard);
        Assert.Equal(cancelDuringRun!.Id, running.Job.Id);
        Assert.True(await shard.RemoveJobAsync(running.Job.Id, CancellationToken.None));
        await first.UnregisterShardAsync(shard, CancellationToken.None);

        var reassigned = Assert.Single(await second.AssignJobShardsAsync(now.AddMinutes(5), int.MaxValue, CancellationToken.None));
        var run = await TakeOneAsync(reassigned);

        Assert.Equal(remaining!.Id, run.Job.Id);
        Assert.Equal(1, await reassigned.GetJobCountAsync());
    }

    [SkippableFact]
    public async Task SlowStartRespectsZeroLimitedUnlimitedAndRepeatedBudgets()
    {
        await using var scope = await fixture.CreateScopeAsync();
        var creator = scope.CreateManager(scope.ActiveSilo);
        var claimant = scope.CreateManager(scope.SecondActiveSilo);
        var now = scope.Now;
        var ownedShard = await claimant.CreateShardAsync(now, now.AddMinutes(1), Metadata("owner", "claimant"), CancellationToken.None);
        var orphanedShardIds = new List<string>();

        for (var i = 0; i < 3; i++)
        {
            var shard = await creator.CreateShardAsync(now.AddMinutes(i), now.AddMinutes(i + 1), Metadata("index", i.ToString()), CancellationToken.None);
            orphanedShardIds.Add(shard.Id);
        }

        scope.SetSiloStatus(scope.ActiveSilo, SiloStatus.Dead);

        var zeroBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), maxNewClaims: 0, CancellationToken.None);
        var firstLimitedBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), maxNewClaims: 1, CancellationToken.None);
        var secondLimitedBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), maxNewClaims: 1, CancellationToken.None);
        var unlimitedBudget = await claimant.AssignJobShardsAsync(now.AddMinutes(10), int.MaxValue, CancellationToken.None);

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

    private static async Task<DurableJob?> ScheduleJobAsync(IJobShard shard, DateTimeOffset dueTime, string jobName, IReadOnlyDictionary<string, string>? metadata = null)
    {
        if (dueTime < shard.StartTime)
        {
            dueTime = shard.StartTime;
        }
        else if (dueTime > shard.EndTime)
        {
            dueTime = shard.EndTime;
        }

        return await shard.TryScheduleJobAsync(CreateRequest(dueTime, jobName, metadata), CancellationToken.None);
    }

    private static async Task<IJobRunContext> TakeOneAsync(IJobShard shard) => (await TakeAsync(shard, 1))[0];

    private static async Task<List<IJobRunContext>> TakeAsync(IJobShard shard, int count)
    {
        var result = new List<IJobRunContext>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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

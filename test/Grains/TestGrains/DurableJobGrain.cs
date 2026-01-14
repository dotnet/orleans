using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class DurableJobGrain : Grain, IDurableJobGrain, IDurableJobHandler
{
    private Dictionary<string, TaskCompletionSource> jobRunStatus = new();
    private Dictionary<string, DateTimeOffset> jobExecutionTimes = new();
    private Dictionary<string, IDurableJobContext> jobContexts = new();
    private Dictionary<string, bool> cancellationTokenStatus = new();
    private readonly ILocalDurableJobManager _localDurableJobManager;
    private readonly ILogger<DurableJobGrain> _logger;

    public DurableJobGrain(ILocalDurableJobManager localDurableJobManager, ILogger<DurableJobGrain> logger)
    {
        _localDurableJobManager = localDurableJobManager;
        _logger = logger;
    }

    public Task<bool> HasJobRan(string jobId)
    {
        return Task.FromResult(jobRunStatus.TryGetValue(jobId, out var taskResult) && taskResult.Task.IsCompleted);
    }

    public Task ExecuteJobAsync(IDurableJobContext ctx, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job {JobId} received at {ReceivedTime}", ctx.Job.Id, DateTime.UtcNow);
        jobExecutionTimes[ctx.Job.Id] = DateTimeOffset.UtcNow;
        jobContexts[ctx.Job.Id] = ctx;
        cancellationTokenStatus[ctx.Job.Id] = cancellationToken.IsCancellationRequested;
        jobRunStatus[ctx.Job.Id].SetResult();
        return Task.CompletedTask;
    }

    public async Task<DurableJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime, IReadOnlyDictionary<string, string> metadata = null)
    {
        var job = await _localDurableJobManager.ScheduleJobAsync(this.GetGrainId(), jobName, scheduledTime, metadata, CancellationToken.None);
        jobRunStatus[job.Id] = new TaskCompletionSource();
        return job;
    }

    public async Task WaitForJobToRun(string jobId)
    {
        if (!jobRunStatus.TryGetValue(jobId, out var taskResult))
        {
            // The job might not have been scheduled on this grain.
            jobRunStatus[jobId] = new TaskCompletionSource();
            taskResult = jobRunStatus[jobId];
        }

        await taskResult.Task;
    }

    public async Task<bool> TryCancelJobAsync(DurableJob job)
    {
        return await _localDurableJobManager.TryCancelDurableJobAsync(job, CancellationToken.None);
    }

    public Task<DateTimeOffset> GetJobExecutionTime(string jobId)
    {
        if (!jobExecutionTimes.TryGetValue(jobId, out var time))
        {
            throw new InvalidOperationException($"Job {jobId} has not executed or was not scheduled on this grain.");
        }

        return Task.FromResult(time);
    }

    public Task<IDurableJobContext> GetJobContext(string jobId)
    {
        if (!jobContexts.TryGetValue(jobId, out var ctx))
        {
            throw new InvalidOperationException($"Job {jobId} has not executed or was not scheduled on this grain.");
        }

        return Task.FromResult(ctx);
    }

    public Task<bool> WasCancellationTokenCancelled(string jobId)
    {
        return Task.FromResult(cancellationTokenStatus.TryGetValue(jobId, out var cancelled) && cancelled);
    }
}

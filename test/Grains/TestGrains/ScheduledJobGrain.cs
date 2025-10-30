using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.ScheduledJobs;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class ScheduledJobGrain : Grain, IScheduledJobGrain, IScheduledJobHandler
{
    private Dictionary<string, TaskCompletionSource> jobRunStatus = new();
    private Dictionary<string, DateTimeOffset> jobExecutionTimes = new();
    private Dictionary<string, IScheduledJobContext> jobContexts = new();
    private Dictionary<string, bool> cancellationTokenStatus = new();
    private readonly ILocalScheduledJobManager _localScheduledJobManager;
    private readonly ILogger<ScheduledJobGrain> _logger;

    public ScheduledJobGrain(ILocalScheduledJobManager localScheduledJobManager, ILogger<ScheduledJobGrain> logger)
    {
        _localScheduledJobManager = localScheduledJobManager;
        _logger = logger;
    }

    public Task<bool> HasJobRan(string jobId)
    {
        return Task.FromResult(jobRunStatus.TryGetValue(jobId, out var taskResult) && taskResult.Task.IsCompleted);
    }

    public Task ExecuteJobAsync(IScheduledJobContext ctx, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job {JobId} received at {ReceivedTime}", ctx.Job.Id, DateTime.UtcNow);
        jobExecutionTimes[ctx.Job.Id] = DateTimeOffset.UtcNow;
        jobContexts[ctx.Job.Id] = ctx;
        cancellationTokenStatus[ctx.Job.Id] = cancellationToken.IsCancellationRequested;
        jobRunStatus[ctx.Job.Id].SetResult();
        return Task.CompletedTask;
    }

    public async Task<IScheduledJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var job = await _localScheduledJobManager.ScheduleJobAsync(this.GetGrainId(), jobName, scheduledTime, metadata, CancellationToken.None);
        jobRunStatus[job.Id] = new TaskCompletionSource();
        return job;
    }

    public async Task WaitForJobToRun(string jobId)
    {
        if (!jobRunStatus.TryGetValue(jobId, out var taskResult))
        {
            throw new InvalidOperationException($"Job {jobId} was not scheduled on this grain.");
        }

        await taskResult.Task;
    }

    public async Task<bool> TryCancelJobAsync(IScheduledJob job)
    {
        return await _localScheduledJobManager.TryCancelScheduledJobAsync(job, CancellationToken.None);
    }

    public Task<DateTimeOffset> GetJobExecutionTime(string jobId)
    {
        if (!jobExecutionTimes.TryGetValue(jobId, out var time))
        {
            throw new InvalidOperationException($"Job {jobId} has not executed or was not scheduled on this grain.");
        }

        return Task.FromResult(time);
    }

    public Task<IScheduledJobContext> GetJobContext(string jobId)
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

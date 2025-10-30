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
        _logger.LogInformation($"Job {ctx.Job.Id} received at {DateTime.UtcNow}");
        jobRunStatus[ctx.Job.Id].SetResult();
        return Task.CompletedTask;
    }

    public async Task<IScheduledJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime)
    {
        var job = await _localScheduledJobManager.ScheduleJobAsync(this.GetGrainId(), jobName, scheduledTime, null, CancellationToken.None);
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
}

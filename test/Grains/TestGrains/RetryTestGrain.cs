using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.DurableJobs;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class RetryTestGrain : Grain, IRetryTestGrain, IDurableJobHandler
{
    private readonly Dictionary<string, TaskCompletionSource> _jobSuccessStatus = new();
    private readonly Dictionary<string, int> _jobExecutionAttempts = new();
    private readonly Dictionary<string, List<int>> _jobDequeueCountHistory = new();
    private readonly Dictionary<string, IDurableJobContext> _finalJobContexts = new();
    private readonly ILocalDurableJobManager _localDurableJobManager;
    private readonly ILogger<RetryTestGrain> _logger;

    public RetryTestGrain(ILocalDurableJobManager localDurableJobManager, ILogger<RetryTestGrain> logger)
    {
        _localDurableJobManager = localDurableJobManager;
        _logger = logger;
    }

    public Task<bool> HasJobSucceeded(string jobId)
    {
        return Task.FromResult(_jobSuccessStatus.TryGetValue(jobId, out var tcs) && tcs.Task.IsCompleted);
    }

    public Task ExecuteJobAsync(IDurableJobContext ctx, CancellationToken cancellationToken)
    {
        var jobId = ctx.Job.Id;
        
        // Initialize tracking if this is the first attempt
        if (!_jobExecutionAttempts.ContainsKey(jobId))
        {
            _jobExecutionAttempts[jobId] = 0;
            _jobDequeueCountHistory[jobId] = new List<int>();
        }

        // Track this attempt
        _jobExecutionAttempts[jobId]++;
        _jobDequeueCountHistory[jobId].Add(ctx.DequeueCount);

        _logger.LogInformation(
            "Job {JobId} execution attempt {Attempt}, DequeueCount: {DequeueCount}",
            jobId,
            _jobExecutionAttempts[jobId],
            ctx.DequeueCount);

        // Check if we should fail based on metadata
        if (ctx.Job.Metadata is not null && ctx.Job.Metadata.TryGetValue("FailUntilAttempt", out var failUntilAttemptStr))
        {
            if (int.TryParse(failUntilAttemptStr, out var failUntilAttempt))
            {
                if (ctx.DequeueCount < failUntilAttempt)
                {
                    _logger.LogWarning(
                        "Job {JobId} intentionally failing on attempt {Attempt} (DequeueCount: {DequeueCount}, FailUntilAttempt: {FailUntilAttempt})",
                        jobId,
                        _jobExecutionAttempts[jobId],
                        ctx.DequeueCount,
                        failUntilAttempt);
                    
                    throw new InvalidOperationException($"Simulated failure for job {jobId} on attempt {_jobExecutionAttempts[jobId]}");
                }
            }
        }

        // Job succeeded
        _logger.LogInformation("Job {JobId} succeeded on attempt {Attempt}", jobId, _jobExecutionAttempts[jobId]);
        _finalJobContexts[jobId] = ctx;
        _jobSuccessStatus[jobId].SetResult();
        
        return Task.CompletedTask;
    }

    public async Task<DurableJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime, IReadOnlyDictionary<string, string> metadata = null)
    {
        var job = await _localDurableJobManager.ScheduleJobAsync(
            this.GetGrainId(),
            jobName,
            scheduledTime,
            metadata,
            CancellationToken.None);
        
        _jobSuccessStatus[job.Id] = new TaskCompletionSource();
        
        return job;
    }

    public async Task WaitForJobToSucceed(string jobId)
    {
        if (!_jobSuccessStatus.TryGetValue(jobId, out var tcs))
        {
            // The job might not have been scheduled on this grain
            _jobSuccessStatus[jobId] = new TaskCompletionSource();
            tcs = _jobSuccessStatus[jobId];
        }

        await tcs.Task;
    }

    public Task<int> GetJobExecutionAttemptCount(string jobId)
    {
        if (!_jobExecutionAttempts.TryGetValue(jobId, out var count))
        {
            throw new InvalidOperationException($"Job {jobId} has not been attempted or was not scheduled on this grain.");
        }

        return Task.FromResult(count);
    }

    public Task<List<int>> GetJobDequeueCountHistory(string jobId)
    {
        if (!_jobDequeueCountHistory.TryGetValue(jobId, out var history))
        {
            throw new InvalidOperationException($"Job {jobId} has not been attempted or was not scheduled on this grain.");
        }

        return Task.FromResult(history);
    }

    public Task<IDurableJobContext> GetFinalJobContext(string jobId)
    {
        if (!_finalJobContexts.TryGetValue(jobId, out var ctx))
        {
            throw new InvalidOperationException($"Job {jobId} has not succeeded or was not scheduled on this grain.");
        }

        return Task.FromResult(ctx);
    }
}

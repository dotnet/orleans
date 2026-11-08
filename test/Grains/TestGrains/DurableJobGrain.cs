using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.DurableJobs;
using Orleans.Serialization.Invocation;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains;

public class DurableJobGrain : Grain, IDurableJobGrain, IDurableJobHandler
{
    private Dictionary<string, TaskCompletionSource> jobRunStatus = new();
    private Dictionary<string, DateTimeOffset> jobExecutionTimes = new();
    private Dictionary<string, IJobRunContext> jobContexts = new();
    private Dictionary<string, bool> cancellationTokenStatus = new();
    private Dictionary<string, string> jobTraceIds = new();
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

    public Task ExecuteJobAsync(IJobRunContext ctx, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Job {JobId} received at {ReceivedTime}", ctx.Job.Id, DateTime.UtcNow);
        jobExecutionTimes[ctx.Job.Id] = DateTimeOffset.UtcNow;
        jobContexts[ctx.Job.Id] = ctx;
        cancellationTokenStatus[ctx.Job.Id] = cancellationToken.IsCancellationRequested;
        jobTraceIds[ctx.Job.Id] = Activity.Current?.TraceId.ToString() ?? string.Empty;
        jobRunStatus[ctx.Job.Id].SetResult();
        return Task.CompletedTask;
    }

    public async Task<DurableJob> ScheduleJobAsync(string jobName, DateTimeOffset scheduledTime, IReadOnlyDictionary<string, string>? metadata = null)
    {
        var request = new ScheduleJobRequest
        {
            Target = this.GetGrainId(),
            JobName = jobName,
            DueTime = scheduledTime,
            Metadata = metadata
        };
        var job = await _localDurableJobManager.ScheduleJobAsync(request, CancellationToken.None);
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

    public async Task<bool> CancelAsync(DurableJob job)
    {
        return await _localDurableJobManager.CancelAsync(job, CancellationToken.None);
    }

    public Task<DateTimeOffset> GetJobExecutionTime(string jobId)
    {
        if (!jobExecutionTimes.TryGetValue(jobId, out var time))
        {
            throw new InvalidOperationException($"Job {jobId} has not executed or was not scheduled on this grain.");
        }

        return Task.FromResult(time);
    }

    public Task<IJobRunContext> GetJobRun(string jobId)
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

    public Task<string> GetJobTraceId(string jobId)
    {
        return Task.FromResult(jobTraceIds.TryGetValue(jobId, out var traceId) ? traceId : string.Empty);
    }
}

[MayInterleave(nameof(ObserveQueuedWrite))]
public class DurableJobOwnerTurnGrain(ILocalDurableJobManager jobs) : Grain, IDurableJobOwnerTurnGrain, IDurableJobTurnCompletionHandler
{
    private readonly TaskCompletionSource _firstStep = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _writeQueued = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completionEffect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _releaseCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _running;
    private int _steps;
    private IJobRunContext? _firstAttempt;

    public Task WriteQueued => _writeQueued.Task;

    public bool ObserveQueuedWrite(IInvokable request)
    {
        if (_running && request.GetMethodName() == nameof(WriteAsync))
        {
            _writeQueued.TrySetResult();
        }

        return false;
    }

    public async Task ScheduleAsync()
    {
        await jobs.ScheduleJobAsync(new ScheduleJobRequest
        {
            Target = this.GetGrainId(),
            JobName = "test-owner-turn",
            DueTime = DateTimeOffset.UtcNow
        }, CancellationToken.None);
    }

    public Task WriteAsync()
    {
        if (_running)
        {
            throw new InvalidOperationException("The ordinary write entered an active job step.");
        }

        return Task.CompletedTask;
    }

    public Task WaitForFirstStepAsync() => _firstStep.Task;

    public Task ReleaseFirstStepAsync()
    {
        _release.TrySetResult();
        return Task.CompletedTask;
    }

    public Task WaitForCompletionEffectAsync() => _completionEffect.Task;

    public Task ReleaseCompletionEffectAsync()
    {
        _releaseCompletion.TrySetResult();
        return Task.CompletedTask;
    }

    public Task<int> WaitForCompletionAsync() => _completed.Task;

    public async ValueTask<DurableJobRunResult> ExecuteJobTurnAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        if (++_steps == 1)
        {
            _firstAttempt = context;
            _running = true;
            try
            {
                _firstStep.TrySetResult();
                await _release.Task.WaitAsync(attemptCancellationToken);
            }
            finally
            {
                _running = false;
            }

            return DurableJobRunResult.ContinueOnOwnerTurn;
        }

        if (_firstAttempt is not { } first || context.Job.Id != first.Job.Id || context.RunId != first.RunId ||
            context.DequeueCount != first.DequeueCount)
        {
            throw new InvalidOperationException("The second owner turn changed the job attempt identity.");
        }

        return DurableJobRunResult.Completed;
    }

    public async Task CompleteJobTurnAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        if (context.RunId != _firstAttempt?.RunId || _steps != 2 || _running)
        {
            throw new InvalidOperationException("The completion effect ran before the owner committed its work.");
        }

        _completionEffect.TrySetResult();
        await _releaseCompletion.Task.WaitAsync(attemptCancellationToken);
        _completed.TrySetResult(_steps);
    }
}

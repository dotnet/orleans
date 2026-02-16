using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Extension interface for grains that can receive durable job invocations.
/// </summary>
internal interface IDurableJobReceiverExtension : IGrainExtension
{
    /// <summary>
    /// Handles a durable job by either starting execution or checking the status of an already running job.
    /// If the job identified by <see cref="IJobRunContext.RunId"/> has not been started, it will be executed.
    /// If it is already running, the current status is returned.
    /// </summary>
    /// <param name="context">The context containing information about the durable job.</param>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous operation and contains the job execution result.</returns>
    [AlwaysInterleave]
    Task<DurableJobRunResult> HandleDurableJobAsync(IJobRunContext context, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed partial class DurableJobReceiverExtension : IDurableJobReceiverExtension
{
    private readonly IGrainContext _grain;
    private readonly ILogger<DurableJobReceiverExtension> _logger;
    private readonly ConcurrentDictionary<string, Task> _runningJobs = new();

    public DurableJobReceiverExtension(IGrainContext grain, ILogger<DurableJobReceiverExtension> logger)
    {
        _grain = grain;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<DurableJobRunResult> HandleDurableJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (_runningJobs.TryGetValue(context.RunId, out var runningTask))
        {
            return GetJobStatus(context, runningTask);
        }

        return StartJobAsync(context, cancellationToken);
    }

    private Task<DurableJobRunResult> StartJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (_grain.GrainInstance is not IDurableJobHandler handler)
        {
            LogGrainDoesNotImplementHandler(_grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IDurableJobHandler");
        }

        var task = handler.ExecuteJobAsync(context, cancellationToken);
        _runningJobs[context.RunId] = task;

        return GetJobStatus(context, task);
    }

    private Task<DurableJobRunResult> GetJobStatus(IJobRunContext context, Task task)
    {
        // Cancellation is cooperative: only terminal task state is authoritative for job outcome.
        if (!task.IsCompleted)
        {
            return Task.FromResult(DurableJobRunResult.PollAfter(TimeSpan.FromSeconds(1)));
        }

        _runningJobs.TryRemove(context.RunId, out _);

        if (task.IsCompletedSuccessfully)
        {
            return Task.FromResult(DurableJobRunResult.Completed);
        }

        if (task.IsFaulted)
        {
            var ex = task.Exception!.InnerException ?? task.Exception;
            LogErrorExecutingDurableJob(ex, context.Job.Id, _grain.GrainId);
            return Task.FromResult(DurableJobRunResult.Failed(ex));
        }

        return Task.FromCanceled<DurableJobRunResult>(new CancellationToken(canceled: true));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing durable job {JobId} on grain {GrainId}")]
    private partial void LogErrorExecutingDurableJob(Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IDurableJobHandler")]
    private partial void LogGrainDoesNotImplementHandler(GrainId grainId);
}


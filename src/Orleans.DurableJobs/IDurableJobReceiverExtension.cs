using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// If the job attempt identified by <see cref="IJobRunContext.Job"/> and <see cref="IJobRunContext.DequeueCount"/> has not been started, it will be executed.
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
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(string JobId, int DequeueCount), JobAttemptState> _jobAttempts = new();
    private readonly ConcurrentQueue<CompletedJobAttempt> _completedJobAttempts = new();
    private int _completedJobAttemptCount;

    private const int MaxCompletedJobAttempts = 65_536;
    private static readonly TimeSpan CompletedJobAttemptRetention = TimeSpan.FromMinutes(1);

    public DurableJobReceiverExtension(IGrainContext grain, ILogger<DurableJobReceiverExtension> logger, TimeProvider timeProvider)
    {
        _grain = grain;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    public Task<DurableJobRunResult> HandleDurableJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        PruneCompletedJobAttempts();

        var key = GetExecutionKey(context);
        var state = _jobAttempts.GetOrAdd(
            key,
            static (_, state) => new JobAttemptState(state.Extension.StartJob(state.Context, state.CancellationToken)),
            (Extension: this, Context: context, CancellationToken: cancellationToken));
        return GetJobStatus(key, context, state);
    }

    private Task<DurableJobRunResult> StartJob(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (_grain.GrainInstance is not IDurableJobHandler handler)
        {
            LogGrainDoesNotImplementHandler(_grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IDurableJobHandler");
        }

        return ExecuteHandlerAsync(handler, context, cancellationToken);
    }

    private async Task<DurableJobRunResult> ExecuteHandlerAsync(IDurableJobHandler handler, IJobRunContext context, CancellationToken cancellationToken)
    {
        var startTimestamp = _timeProvider.GetTimestamp();
        DurableJobsInstruments.OnHandlerExecutionStarted();
        try
        {
            await handler.ExecuteJobAsync(context, cancellationToken);
            DurableJobsInstruments.OnHandlerExecutionCompleted(_timeProvider.GetElapsedTime(startTimestamp));
            return DurableJobRunResult.Completed;
        }
        catch (OperationCanceledException)
        {
            DurableJobsInstruments.OnHandlerExecutionCanceled(_timeProvider.GetElapsedTime(startTimestamp));
            throw;
        }
        catch (Exception exception)
        {
            DurableJobsInstruments.OnHandlerExecutionFailed(_timeProvider.GetElapsedTime(startTimestamp));
            LogErrorExecutingDurableJob(exception, context.Job.Id, _grain.GrainId);
            return DurableJobRunResult.Failed(exception);
        }
    }

    private Task<DurableJobRunResult> GetJobStatus((string JobId, int DequeueCount) key, IJobRunContext context, JobAttemptState state)
    {
        // Cancellation is cooperative: only terminal task state is authoritative for job outcome.
        if (!state.Task.IsCompleted)
        {
            return Task.FromResult(DurableJobRunResult.PollAfter(TimeSpan.FromSeconds(1)));
        }

        RecordCompletedJobAttempt(key, state);

        if (state.Task.IsCompletedSuccessfully)
        {
            return state.Task;
        }

        if (state.Task.IsFaulted)
        {
            var ex = state.Task.Exception!.InnerException ?? state.Task.Exception;
            LogErrorExecutingDurableJob(ex, context.Job.Id, _grain.GrainId);
            return Task.FromResult(DurableJobRunResult.Failed(ex));
        }

        return Task.FromCanceled<DurableJobRunResult>(new CancellationToken(canceled: true));
    }

    private void RecordCompletedJobAttempt((string JobId, int DequeueCount) key, JobAttemptState state)
    {
        if (Interlocked.CompareExchange(ref state.CompletionRecorded, 1, 0) == 0)
        {
            var completedTimestamp = _timeProvider.GetTimestamp();
            Volatile.Write(ref state.CompletedTimestamp, completedTimestamp);
            _completedJobAttempts.Enqueue(new CompletedJobAttempt(key, completedTimestamp));
            Interlocked.Increment(ref _completedJobAttemptCount);
        }

        PruneCompletedJobAttempts();
    }

    private void PruneCompletedJobAttempts()
    {
        var now = _timeProvider.GetTimestamp();
        while (_completedJobAttempts.TryPeek(out var completedAttempt))
        {
            var expired = _timeProvider.GetElapsedTime(completedAttempt.CompletedTimestamp, now) >= CompletedJobAttemptRetention;
            var overLimit = Volatile.Read(ref _completedJobAttemptCount) > MaxCompletedJobAttempts;
            if (!expired && !overLimit)
            {
                return;
            }

            if (!_completedJobAttempts.TryDequeue(out completedAttempt))
            {
                return;
            }

            if (_jobAttempts.TryGetValue(completedAttempt.Key, out var state)
                && state.Task.IsCompleted
                && Volatile.Read(ref state.CompletedTimestamp) == completedAttempt.CompletedTimestamp)
            {
                ((ICollection<KeyValuePair<(string JobId, int DequeueCount), JobAttemptState>>)_jobAttempts).Remove(
                    new KeyValuePair<(string JobId, int DequeueCount), JobAttemptState>(completedAttempt.Key, state));
            }

            Interlocked.Decrement(ref _completedJobAttemptCount);
        }
    }

    private static (string JobId, int DequeueCount) GetExecutionKey(IJobRunContext context)
        => (context.Job.Id, context.DequeueCount);

    private sealed class JobAttemptState(Task<DurableJobRunResult> task)
    {
        public Task<DurableJobRunResult> Task { get; } = task;

        public int CompletionRecorded;

        public long CompletedTimestamp;
    }

    private readonly record struct CompletedJobAttempt((string JobId, int DequeueCount) Key, long CompletedTimestamp);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing durable job {JobId} on grain {GrainId}")]
    private partial void LogErrorExecutingDurableJob(Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IDurableJobHandler")]
    private partial void LogGrainDoesNotImplementHandler(GrainId grainId);
}

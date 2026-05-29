using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Concurrency;
using Orleans.Hosting;
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
    ValueTask<DurableJobRunResult> HandleDurableJobAsync(IJobRunContext context, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed partial class DurableJobReceiverExtension : IDurableJobReceiverExtension
{
    private readonly IGrainContext _grain;
    private readonly ILogger<DurableJobReceiverExtension> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly DurableJobsOptions _options;
    private readonly Dictionary<(string JobId, int DequeueCount), JobAttemptState> _jobAttempts = [];
    private readonly Queue<CompletedJobAttempt> _completedJobAttempts = new();
    private int _completedJobAttemptCount;

    private const int MaxCompletedJobAttempts = 65_536;
    private static readonly TimeSpan CompletedJobAttemptRetention = TimeSpan.FromMinutes(1);

    public DurableJobReceiverExtension(
        IGrainContext grain,
        ILogger<DurableJobReceiverExtension> logger,
        TimeProvider timeProvider,
        IOptions<DurableJobsOptions> options)
    {
        _grain = grain;
        _logger = logger;
        _timeProvider = timeProvider;
        _options = options.Value;
    }

    /// <inheritdoc />
    public ValueTask<DurableJobRunResult> HandleDurableJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        PruneCompletedJobAttempts();

        var key = GetExecutionKey(context);
        JobAttemptState state;
        ref var stateRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_jobAttempts, key, out var exists);
        if (!exists)
        {
            Debug.Assert(stateRef is null);
            stateRef = new JobAttemptState(StartJob(context, cancellationToken));
        }

        Debug.Assert(stateRef is not null);
        state = stateRef;
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
            // Cancellation can be retried.
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

    private ValueTask<DurableJobRunResult> GetJobStatus((string JobId, int DequeueCount) key, IJobRunContext context, JobAttemptState state)
    {
        // Cancellation is cooperative: only terminal task state is authoritative for job outcome.
        if (!state.Task.IsCompleted)
        {
            return new(DurableJobRunResult.PollAfter(_options.JobStatusPollInterval));
        }

        RecordCompletedJobAttempt(key, state);

        if (state.Task.IsCompletedSuccessfully)
        {
            return new(state.Task);
        }

        if (state.Task.IsFaulted)
        {
            var ex = state.Task.Exception!.InnerException ?? state.Task.Exception;
            LogErrorExecutingDurableJob(ex, context.Job.Id, _grain.GrainId);
            return new(DurableJobRunResult.Failed(ex));
        }

        return ValueTask.FromCanceled<DurableJobRunResult>(new CancellationToken(canceled: true));
    }

    private void RecordCompletedJobAttempt((string JobId, int DequeueCount) key, JobAttemptState state)
    {
        if (!state.CompletionRecorded)
        {
            state.CompletionRecorded = true;
            var completedTimestamp = _timeProvider.GetTimestamp();
            state.CompletedTimestamp = completedTimestamp;
            _completedJobAttempts.Enqueue(new CompletedJobAttempt(key, completedTimestamp));
            _completedJobAttemptCount++;
        }

        PruneCompletedJobAttempts();
    }

    private void PruneCompletedJobAttempts()
    {
        var now = _timeProvider.GetTimestamp();
        while (_completedJobAttempts.TryPeek(out var completedAttempt))
        {
            var expired = _timeProvider.GetElapsedTime(completedAttempt.CompletedTimestamp, now) >= CompletedJobAttemptRetention;
            var overLimit = _completedJobAttemptCount > MaxCompletedJobAttempts;
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
                && state.CompletedTimestamp == completedAttempt.CompletedTimestamp)
            {
                ((ICollection<KeyValuePair<(string JobId, int DequeueCount), JobAttemptState>>)_jobAttempts).Remove(
                    new KeyValuePair<(string JobId, int DequeueCount), JobAttemptState>(completedAttempt.Key, state));
            }

            _completedJobAttemptCount--;
        }
    }

    private static (string JobId, int DequeueCount) GetExecutionKey(IJobRunContext context)
        => (context.Job.Id, context.DequeueCount);

    private sealed class JobAttemptState(Task<DurableJobRunResult> task)
    {
        public Task<DurableJobRunResult> Task { get; } = task;

        public bool CompletionRecorded;

        public long CompletedTimestamp;
    }

    private readonly record struct CompletedJobAttempt((string JobId, int DequeueCount) Key, long CompletedTimestamp);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing durable job {JobId} on grain {GrainId}")]
    private partial void LogErrorExecutingDurableJob(Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IDurableJobHandler")]
    private partial void LogGrainDoesNotImplementHandler(GrainId grainId);
}

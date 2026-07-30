using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Orleans.Concurrency;

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

internal interface IDurableJobExecutionExtension : IGrainExtension
{
    Task<DurableJobRunResult> ExecuteDurableJobAsync(IJobRunContext context, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed partial class DurableJobReceiverExtension : IDurableJobReceiverExtension
{
    private const int MaxCompletedJobAttempts = 65_536;
    private static readonly TimeSpan CompletedJobAttemptRetention = TimeSpan.FromMinutes(1);

    private readonly IGrainContext _grain;
    private readonly DurableJobReceiverExtensionShared _shared;
    private readonly Dictionary<(string ShardId, string JobId, int DequeueCount), JobAttemptState> _jobAttempts = [];
    private readonly Queue<CompletedJobAttempt> _completedJobAttempts = new();
    private int _completedJobAttemptCount;

    public DurableJobReceiverExtension(IGrainContext grain, DurableJobReceiverExtensionShared shared)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(shared);

        _grain = grain;
        _shared = shared;
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
            try
            {
                stateRef = new JobAttemptState(StartJob(context, cancellationToken));
            }
            catch
            {
                _jobAttempts.Remove(key);
                throw;
            }
        }

        Debug.Assert(stateRef is not null);
        state = stateRef;
        return GetJobStatusAsync(key, context, state, newJob: !exists);
    }

    private Task<DurableJobRunResult> StartJob(IJobRunContext context, CancellationToken cancellationToken)
    {
        using var _ = RequestContext.SuppressCallChainReentrancy();
        var executionTarget = _shared.GrainFactory.GetGrain<IDurableJobExecutionExtension>(_grain.GrainId);
        return executionTarget.ExecuteDurableJobAsync(context, cancellationToken);
    }

    private ValueTask<DurableJobRunResult> GetJobStatusAsync((string ShardId, string JobId, int DequeueCount) key, IJobRunContext context, JobAttemptState state, bool newJob)
    {
        // Cancellation is cooperative: only terminal task state is authoritative for job outcome.
        if (!state.Task.IsCompleted)
        {
            if (newJob)
            {
                // For the first attempt, to reduce RPC, we wait for the polling interval or half the response timeout for the task to complete.
                // This saves a back-and-forth for the common case where a job completes quickly.
                return LongPollGetJobStatusAsync(key, context, state);
            }

            return new(DurableJobRunResult.PollAfter(_shared.Options.JobStatusPollInterval));
        }

        if (state.Task.IsCanceled)
        {
            RemoveCanceledJobAttempt(key, state);
            return ValueTask.FromCanceled<DurableJobRunResult>(new CancellationToken(canceled: true));
        }

        RecordCompletedJobAttempt(key, state);

        if (state.Task.IsCompletedSuccessfully)
        {
            return new(state.Task);
        }

        if (state.Task.IsFaulted)
        {
            var ex = state.Task.Exception!.InnerException ?? state.Task.Exception;
            LogErrorExecutingDurableJob(_shared.Logger, ex, context.Job.Id, _grain.GrainId);
            return new(DurableJobRunResult.Failed(ex));
        }

        throw new UnreachableException();

        async ValueTask<DurableJobRunResult> LongPollGetJobStatusAsync((string ShardId, string JobId, int DequeueCount) key, IJobRunContext context, JobAttemptState state)
        {
            if (!state.Task.IsCompleted)
            {
                using var cts = new CancellationTokenSource();
                var longPollDuration = TimeSpan.FromTicks(Math.Min(_shared.MessagingOptions.ResponseTimeout.Divide(2).Ticks, _shared.Options.JobStatusPollInterval.Ticks));
                var delayTask = Task.Delay(longPollDuration, _shared.TimeProvider, cts.Token);
                if (await Task.WhenAny(delayTask, state.Task) == state.Task)
                {
                    await cts.CancelAsync();
                }

                if (!state.Task.IsCompleted)
                {
                    return DurableJobRunResult.PollAfter(_shared.Options.JobStatusPollInterval);
                }
            }

            if (state.Task.IsCanceled)
            {
                RemoveCanceledJobAttempt(key, state);
                return await state.Task;
            }

            RecordCompletedJobAttempt(key, state);

            if (state.Task.IsFaulted)
            {
                var ex = state.Task.Exception!.InnerException ?? state.Task.Exception;
                LogErrorExecutingDurableJob(_shared.Logger, ex, context.Job.Id, _grain.GrainId);
                return DurableJobRunResult.Failed(ex);
            }

            // Completed successfully or canceled.
            return await state.Task;
        }
    }

    private void RemoveCanceledJobAttempt((string ShardId, string JobId, int DequeueCount) key, JobAttemptState state)
        => ((ICollection<KeyValuePair<(string ShardId, string JobId, int DequeueCount), JobAttemptState>>)_jobAttempts).Remove(
            new KeyValuePair<(string ShardId, string JobId, int DequeueCount), JobAttemptState>(key, state));

    private void RecordCompletedJobAttempt((string ShardId, string JobId, int DequeueCount) key, JobAttemptState state)
    {
        if (!state.CompletionRecorded)
        {
            state.CompletionRecorded = true;
            var completedTimestamp = _shared.TimeProvider.GetTimestamp();
            state.CompletedTimestamp = completedTimestamp;
            _completedJobAttempts.Enqueue(new CompletedJobAttempt(key, completedTimestamp));
            _completedJobAttemptCount++;
        }

        PruneCompletedJobAttempts();
    }

    private void PruneCompletedJobAttempts()
    {
        var now = _shared.TimeProvider.GetTimestamp();
        while (_completedJobAttempts.TryPeek(out var completedAttempt))
        {
            var expired = _shared.TimeProvider.GetElapsedTime(completedAttempt.CompletedTimestamp, now) >= CompletedJobAttemptRetention;
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
                ((ICollection<KeyValuePair<(string ShardId, string JobId, int DequeueCount), JobAttemptState>>)_jobAttempts).Remove(
                    new KeyValuePair<(string ShardId, string JobId, int DequeueCount), JobAttemptState>(completedAttempt.Key, state));
            }

            _completedJobAttemptCount--;
        }
    }

    private static (string ShardId, string JobId, int DequeueCount) GetExecutionKey(IJobRunContext context)
        => (context.Job.ShardId, context.Job.Id, context.DequeueCount);

    private sealed class JobAttemptState(Task<DurableJobRunResult> task)
    {
        public Task<DurableJobRunResult> Task { get; } = task;

        public bool CompletionRecorded;

        public long CompletedTimestamp;
    }

    private readonly record struct CompletedJobAttempt((string ShardId, string JobId, int DequeueCount) Key, long CompletedTimestamp);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error invoking durable job {JobId} on grain {GrainId}")]
    private static partial void LogErrorExecutingDurableJob(ILogger logger, Exception exception, string jobId, GrainId grainId);

}

internal sealed partial class DurableJobExecutionExtension(IGrainContext grain, DurableJobReceiverExtensionShared shared) : IDurableJobExecutionExtension
{
    public async Task<DurableJobRunResult> ExecuteDurableJobAsync(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (grain.GrainInstance is not IDurableJobHandler handler)
        {
            LogGrainDoesNotImplementHandler(shared.Logger, grain.GrainId);
            throw new InvalidOperationException($"Grain {grain.GrainId} does not implement IDurableJobHandler");
        }

        using var tracker = shared.BeginHandlerExecution(context);
        try
        {
            await handler.ExecuteJobAsync(context, cancellationToken);
            tracker.Completed();
            return DurableJobRunResult.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            tracker.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            tracker.Failed(exception);
            LogErrorExecutingDurableJob(shared.Logger, exception, context.Job.Id, grain.GrainId);
            return DurableJobRunResult.Failed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing durable job {JobId} on grain {GrainId}")]
    private static partial void LogErrorExecutingDurableJob(ILogger logger, Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IDurableJobHandler")]
    private static partial void LogGrainDoesNotImplementHandler(ILogger logger, GrainId grainId);
}

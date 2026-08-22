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
    /// Handles a durable job by either starting execution or checking the status of an execution which remains in progress.
    /// If the job attempt identified by <see cref="IJobRunContext.Job"/> and <see cref="IJobRunContext.DequeueCount"/> has not been started, it will be executed.
    /// If it remains in progress, the current status is returned.
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
    private const int MaxCompletedJobAttempts = 65_536;
    private static readonly TimeSpan CompletedJobAttemptRetention = TimeSpan.FromMinutes(1);

    private readonly IGrainContext _grain;
    private readonly DurableJobReceiverExtensionShared _shared;
    private readonly DurableJobHandlerRegistry? _featureHandlers;
    private readonly Dictionary<(string JobId, long ExecutionGeneration, int DequeueCount), JobAttemptState> _jobAttempts = [];
    private readonly Queue<CompletedJobAttempt> _completedJobAttempts = new();
    private int _completedJobAttemptCount;

    public DurableJobReceiverExtension(
        IGrainContext grain,
        DurableJobReceiverExtensionShared shared,
        DurableJobHandlerRegistry? featureHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(shared);

        _grain = grain;
        _shared = shared;
        _featureHandlers = featureHandlers;
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
        return GetJobStatusAsync(key, context, state, newJob: !exists);
    }

    private Task<DurableJobRunResult> StartJob(IJobRunContext context, CancellationToken cancellationToken)
    {
        if (_featureHandlers?.TryGetHandler(context.Job.Name, out var featureHandler) is true)
        {
            return ExecuteFeatureHandlerAsync(featureHandler, context, cancellationToken);
        }

        if (_grain.GrainInstance is not IDurableJobHandler handler)
        {
            LogGrainDoesNotImplementHandler(_shared.Logger, _grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IDurableJobHandler");
        }

        return ExecuteHandlerAsync(handler, context, cancellationToken);
    }

    private async Task<DurableJobRunResult> ExecuteFeatureHandlerAsync(
        IDurableJobFeatureHandler handler,
        IJobRunContext context,
        CancellationToken cancellationToken)
    {
        using var tracker = _shared.BeginHandlerExecution(context);
        try
        {
            var result = await handler.ExecuteJobAsync(context, cancellationToken);
            tracker.Completed();
            return result ?? throw new InvalidOperationException(
                $"Durable job feature handler for '{context.Job.Name}' returned a null result.");
        }
        catch (OperationCanceledException)
        {
            tracker.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            tracker.Failed(exception);
            LogErrorExecutingDurableJob(_shared.Logger, exception, context.Job.Id, _grain.GrainId);
            return DurableJobRunResult.Failed(exception);
        }
    }

    private async Task<DurableJobRunResult> ExecuteHandlerAsync(IDurableJobHandler handler, IJobRunContext context, CancellationToken cancellationToken)
    {
        using var tracker = _shared.BeginHandlerExecution(context);
        try
        {
            await handler.ExecuteJobAsync(context, cancellationToken);
            tracker.Completed();
            return DurableJobRunResult.Completed;
        }
        catch (OperationCanceledException)
        {
            // Cancellation can be retried.
            tracker.Canceled();
            throw;
        }
        catch (Exception exception)
        {
            tracker.Failed(exception);
            LogErrorExecutingDurableJob(_shared.Logger, exception, context.Job.Id, _grain.GrainId);
            return DurableJobRunResult.Failed(exception);
        }
    }

    private ValueTask<DurableJobRunResult> GetJobStatusAsync(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        IJobRunContext context,
        JobAttemptState state,
        bool newJob)
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

            return new(DurableJobRunResult.InProgress(_shared.Options.JobStatusPollInterval));
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

        return ValueTask.FromCanceled<DurableJobRunResult>(new CancellationToken(canceled: true));

        async ValueTask<DurableJobRunResult> LongPollGetJobStatusAsync(
            (string JobId, long ExecutionGeneration, int DequeueCount) key,
            IJobRunContext context,
            JobAttemptState state)
        {
            if (!state.Task.IsCompleted)
            {
                using var cts = new CancellationTokenSource();
                var longPollDuration = TimeSpan.FromTicks(Math.Min(_shared.MessagingOptions.ResponseTimeout.Divide(2).Ticks, _shared.Options.JobStatusPollInterval.Ticks));
                await Task.WhenAny(Task.Delay(longPollDuration, cts.Token), state.Task);

                if (!state.Task.IsCompleted)
                {
                    return DurableJobRunResult.InProgress(_shared.Options.JobStatusPollInterval);
                }
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

    private void RecordCompletedJobAttempt((string JobId, long ExecutionGeneration, int DequeueCount) key, JobAttemptState state)
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
                ((ICollection<KeyValuePair<(string JobId, long ExecutionGeneration, int DequeueCount), JobAttemptState>>)_jobAttempts).Remove(
                    new KeyValuePair<(string JobId, long ExecutionGeneration, int DequeueCount), JobAttemptState>(completedAttempt.Key, state));
            }

            _completedJobAttemptCount--;
        }
    }

    private static (string JobId, long ExecutionGeneration, int DequeueCount) GetExecutionKey(IJobRunContext context)
        => (context.Job.Id, context.Job.ExecutionGeneration, context.DequeueCount);

    private sealed class JobAttemptState(Task<DurableJobRunResult> task)
    {
        public Task<DurableJobRunResult> Task { get; } = task;

        public bool CompletionRecorded;

        public long CompletedTimestamp;
    }

    private readonly record struct CompletedJobAttempt(
        (string JobId, long ExecutionGeneration, int DequeueCount) Key,
        long CompletedTimestamp);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing durable job {JobId} on grain {GrainId}")]
    private static partial void LogErrorExecutingDurableJob(ILogger logger, Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IDurableJobHandler")]
    private static partial void LogGrainDoesNotImplementHandler(ILogger logger, GrainId grainId);
}

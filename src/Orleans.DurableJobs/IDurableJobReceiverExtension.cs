using System.Diagnostics;
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
    /// Concurrent deliveries for the same job attempt share the active invocation. Once an invocation reaches
    /// a terminal disposition, later deliveries return the cached result for a bounded retention period.
    /// </summary>
    /// <param name="context">The context containing information about the durable job.</param>
    /// <param name="cancellationToken">
    /// Cancels the caller's status request. It does not cancel an execution which has already started;
    /// activation shutdown supplies the separate execution cancellation token.
    /// </param>
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
    private readonly IDurableJobHandlerLookup _featureHandlers;
    private readonly object _lock = new();
    private readonly Dictionary<(string JobId, long ExecutionGeneration, int DequeueCount), JobAttemptState> _jobAttempts = [];
    private readonly Queue<CompletedJobAttempt> _completedJobAttempts = [];
    private int _completedJobAttemptCount;

    public DurableJobReceiverExtension(
        IGrainContext grain,
        DurableJobReceiverExtensionShared shared,
        IDurableJobHandlerLookup? featureHandlers = null)
    {
        ArgumentNullException.ThrowIfNull(grain);
        ArgumentNullException.ThrowIfNull(shared);

        _grain = grain;
        _shared = shared;
        _featureHandlers = featureHandlers ?? new DurableJobHandlerRegistry();
    }

    /// <inheritdoc />
    public ValueTask<DurableJobRunResult> HandleDurableJobAsync(
        IJobRunContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        (string JobId, long ExecutionGeneration, int DequeueCount) key;
        JobAttemptState state;
        bool newJob;
        lock (_lock)
        {
            PruneCompletedJobAttemptsUnderLock();
            key = GetExecutionKey(context);
            if (!_jobAttempts.TryGetValue(key, out state!))
            {
                state = new JobAttemptState(StartJob(context));
                _jobAttempts.Add(key, state);
                newJob = true;
            }
            else
            {
                newJob = false;
                if (IsReadyToPoll(state))
                {
                    state.Task = StartJob(context);
                    state.PollRequested = false;
                    state.CompletionRecorded = false;
                }
            }
        }

        return GetJobStatusAsync(key, context, state, newJob, cancellationToken);
    }

    private bool IsReadyToPoll(JobAttemptState state) =>
        state.Task.IsCompletedSuccessfully
        && state.Task.Result.IsInProgress
        && state.PollRequested
        && _shared.TimeProvider.GetElapsedTime(state.PollTimestamp, _shared.TimeProvider.GetTimestamp()) >= state.PollAfterDelay;

    private Task<DurableJobRunResult> StartJob(IJobRunContext context)
    {
        if (_featureHandlers.TryGetHandler(context.Job.Name, out var featureHandler))
        {
            return _featureHandlers.StartExecution(
                token => ExecuteFeatureHandlerAsync(featureHandler, context, token));
        }

        if (_grain.GrainInstance is not IDurableJobHandler handler)
        {
            LogGrainDoesNotImplementHandler(_shared.Logger, _grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IDurableJobHandler");
        }

        return _featureHandlers.StartExecution(
            token => ExecuteHandlerAsync(handler, context, token));
    }

    private Task<DurableJobRunResult> ExecuteFeatureHandlerAsync(
        IDurableJobFeatureHandler handler,
        IJobRunContext context,
        CancellationToken executionToken) =>
        ExecuteHandlerAsync(
            context,
            executionToken,
            () => handler.ExecuteJobAsync(context, executionToken));

    private Task<DurableJobRunResult> ExecuteHandlerAsync(
        IDurableJobHandler handler,
        IJobRunContext context,
        CancellationToken executionToken)
    {
        return ExecuteHandlerAsync(context, executionToken, ExecuteAsync);

        async ValueTask<DurableJobRunResult> ExecuteAsync()
        {
            await handler.ExecuteJobAsync(context, executionToken);
            return DurableJobRunResult.Completed;
        }
    }

    private async Task<DurableJobRunResult> ExecuteHandlerAsync(
        IJobRunContext context,
        CancellationToken executionToken,
        Func<ValueTask<DurableJobRunResult>> execute)
    {
        using var tracker = _shared.BeginHandlerExecution(context);
        try
        {
            var result = await execute()
                ?? throw new InvalidOperationException($"Durable job handler for '{context.Job.Name}' returned a null result.");
            tracker.RecordResult(result);
            return result;
        }
        catch (OperationCanceledException) when (executionToken.IsCancellationRequested)
        {
            tracker.AttemptCanceled();
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
        bool newJob,
        CancellationToken cancellationToken)
    {
        if (!state.Task.IsCompleted)
        {
            if (newJob)
            {
                return LongPollGetJobStatusAsync(key, context, state, cancellationToken);
            }

            return new(DurableJobRunResult.InProgress(_shared.Options.JobStatusPollInterval));
        }

        return ResolveCompletedJobStatusAsync(key, context, state);

        async ValueTask<DurableJobRunResult> LongPollGetJobStatusAsync(
            (string JobId, long ExecutionGeneration, int DequeueCount) key,
            IJobRunContext context,
            JobAttemptState state,
            CancellationToken cancellationToken)
        {
            if (!state.Task.IsCompleted)
            {
                using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var longPollDuration = TimeSpan.FromTicks(
                    Math.Min(
                        _shared.MessagingOptions.ResponseTimeout.Divide(2).Ticks,
                        _shared.Options.JobStatusPollInterval.Ticks));
                var delayTask = Task.Delay(longPollDuration, _shared.TimeProvider, timeoutCancellation.Token);
                var completedTask = await Task.WhenAny(delayTask, state.Task);
                if (completedTask == delayTask)
                {
                    await delayTask;
                    return DurableJobRunResult.InProgress(_shared.Options.JobStatusPollInterval);
                }

                timeoutCancellation.Cancel();
            }

            return await ResolveCompletedJobStatusAsync(key, context, state);
        }
    }

    private async ValueTask<DurableJobRunResult> ResolveCompletedJobStatusAsync(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        IJobRunContext context,
        JobAttemptState state)
    {
        if (state.Task.IsCompletedSuccessfully)
        {
            return GetSuccessfulResult(key, state);
        }

        RecordCompletedJobAttempt(key, state);
        if (state.Task.IsFaulted)
        {
            var exception = state.Task.Exception!.InnerException ?? state.Task.Exception;
            LogErrorExecutingDurableJob(_shared.Logger, exception, context.Job.Id, _grain.GrainId);
            return DurableJobRunResult.Failed(exception);
        }

        return await state.Task;
    }

    private DurableJobRunResult GetSuccessfulResult(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        JobAttemptState state)
    {
        var result = state.Task.Result;
        lock (_lock)
        {
            if (result.IsInProgress)
            {
                if (!state.PollRequested)
                {
                    state.PollRequested = true;
                    state.PollTimestamp = _shared.TimeProvider.GetTimestamp();
                    state.PollAfterDelay = result.PollAfterDelay.Value;
                }
            }
            else
            {
                RecordCompletedJobAttemptUnderLock(key, state);
            }
        }

        return result;
    }

    private void RecordCompletedJobAttempt(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        JobAttemptState state)
    {
        lock (_lock)
        {
            RecordCompletedJobAttemptUnderLock(key, state);
        }
    }

    private void RecordCompletedJobAttemptUnderLock(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        JobAttemptState state)
    {
        if (!state.CompletionRecorded)
        {
            state.CompletionRecorded = true;
            var completedTimestamp = _shared.TimeProvider.GetTimestamp();
            state.CompletedTimestamp = completedTimestamp;
            _completedJobAttempts.Enqueue(new(key, completedTimestamp));
            _completedJobAttemptCount++;
        }

        PruneCompletedJobAttemptsUnderLock();
    }

    private void PruneCompletedJobAttemptsUnderLock()
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

            _completedJobAttempts.Dequeue();
            if (_jobAttempts.TryGetValue(completedAttempt.Key, out var state)
                && state.CompletedTimestamp == completedAttempt.CompletedTimestamp)
            {
                _jobAttempts.Remove(completedAttempt.Key);
            }

            _completedJobAttemptCount--;
        }
    }

    private static (string JobId, long ExecutionGeneration, int DequeueCount) GetExecutionKey(IJobRunContext context)
        => (context.Job.Id, context.Job.ExecutionGeneration, context.DequeueCount);

    internal sealed class TestAccessor(DurableJobReceiverExtension extension)
    {
        public Task<DurableJobRunResult>? GetAttemptTask(IJobRunContext context)
        {
            lock (extension._lock)
            {
                return extension._jobAttempts.TryGetValue(GetExecutionKey(context), out var state) ? state.Task : null;
            }
        }
    }

    private sealed class JobAttemptState(Task<DurableJobRunResult> task)
    {
        public Task<DurableJobRunResult> Task { get; set; } = task;

        public bool PollRequested;

        public long PollTimestamp;

        public TimeSpan PollAfterDelay;

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

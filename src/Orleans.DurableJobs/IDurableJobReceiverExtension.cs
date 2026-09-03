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
    /// a terminal disposition, a later delivery starts a new invocation.
    /// </summary>
    /// <param name="context">The context containing information about the durable job.</param>
    /// <param name="attemptCancellationToken">
    /// A token which cooperatively requests cancellation of this execution attempt.
    /// Attempt cancellation leaves the durable job eligible for redelivery.
    /// </param>
    /// <returns>A task that represents the asynchronous operation and contains the job execution result.</returns>
    [AlwaysInterleave]
    ValueTask<DurableJobRunResult> HandleDurableJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken);
}

/// <inheritdoc />
internal sealed partial class DurableJobReceiverExtension : IDurableJobReceiverExtension
{
    private readonly IGrainContext _grain;
    private readonly DurableJobReceiverExtensionShared _shared;
    private readonly IDurableJobHandlerLookup _featureHandlers;
    private readonly Dictionary<(string JobId, long ExecutionGeneration, int DequeueCount), JobAttemptState> _jobAttempts = [];

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
    public ValueTask<DurableJobRunResult> HandleDurableJobAsync(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = GetExecutionKey(context);
        var newJob = false;
        if (!_jobAttempts.TryGetValue(key, out var state))
        {
            state = new JobAttemptState(StartJob(context, attemptCancellationToken));
            _jobAttempts.Add(key, state);
            newJob = true;
        }
        else if (state.Task.IsCanceled && !attemptCancellationToken.IsCancellationRequested)
        {
            state = new JobAttemptState(StartJob(context, attemptCancellationToken));
            _jobAttempts[key] = state;
            newJob = true;
        }
        else if (IsReadyToPoll(state))
        {
            state = new JobAttemptState(StartJob(context, attemptCancellationToken));
            _jobAttempts[key] = state;
            newJob = true;
        }

        Debug.Assert(state is not null);
        return GetJobStatusAsync(key, context, state, newJob, attemptCancellationToken);
    }

    private bool IsReadyToPoll(JobAttemptState state) =>
        state.PollRequested
        && _shared.TimeProvider.GetElapsedTime(state.PollTimestamp, _shared.TimeProvider.GetTimestamp()) >= state.PollAfterDelay;

    private Task<DurableJobRunResult> StartJob(IJobRunContext context, CancellationToken attemptCancellationToken)
    {
        if (_featureHandlers.TryGetHandler(context.Job.Name, out var featureHandler))
        {
            return ExecuteFeatureHandlerAsync(featureHandler, context, attemptCancellationToken);
        }

        if (_grain.GrainInstance is not IDurableJobHandler handler)
        {
            LogGrainDoesNotImplementHandler(_shared.Logger, _grain.GrainId);
            throw new InvalidOperationException($"Grain {_grain.GrainId} does not implement IDurableJobHandler");
        }

        return ExecuteHandlerAsync(handler, context, attemptCancellationToken);
    }

    private Task<DurableJobRunResult> ExecuteFeatureHandlerAsync(
        IDurableJobFeatureHandler handler,
        IJobRunContext context,
        CancellationToken attemptCancellationToken) =>
        ExecuteHandlerAsync(
            context,
            attemptCancellationToken,
            () => handler.ExecuteJobAsync(context, attemptCancellationToken));

    private Task<DurableJobRunResult> ExecuteHandlerAsync(
        IDurableJobHandler handler,
        IJobRunContext context,
        CancellationToken attemptCancellationToken)
    {
        return ExecuteHandlerAsync(context, attemptCancellationToken, ExecuteAsync);

        async ValueTask<DurableJobRunResult> ExecuteAsync()
        {
            await handler.ExecuteJobAsync(context, attemptCancellationToken);
            return DurableJobRunResult.Completed;
        }
    }

    private async Task<DurableJobRunResult> ExecuteHandlerAsync(
        IJobRunContext context,
        CancellationToken attemptCancellationToken,
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
        catch (OperationCanceledException) when (attemptCancellationToken.IsCancellationRequested)
        {
            // Attempt cancellation leaves the durable job eligible for redelivery.
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

    private async ValueTask<DurableJobRunResult> GetJobStatusAsync(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        IJobRunContext context,
        JobAttemptState state,
        bool newJob,
        CancellationToken attemptCancellationToken)
    {
        // Cancellation is cooperative: only terminal task state is authoritative for job outcome.
        if (!state.Task.IsCompleted)
        {
            if (newJob)
            {
                // For the first attempt, to reduce RPC, we wait for the polling interval or half the response timeout for the task to complete.
                // This saves a back-and-forth for the common case where a job completes quickly.
                return await LongPollGetJobStatusAsync(key, context, state, attemptCancellationToken);
            }

            return DurableJobRunResult.InProgress(_shared.Options.JobStatusPollInterval);
        }

        if (state.Task.IsCompletedSuccessfully)
        {
            return GetSuccessfulResult(key, state, await state.Task);
        }

        RemoveJobAttempt(key, state);

        if (state.Task.IsFaulted)
        {
            var ex = state.Task.Exception!.InnerException ?? state.Task.Exception;
            LogErrorExecutingDurableJob(_shared.Logger, ex, context.Job.Id, _grain.GrainId);
            return DurableJobRunResult.Failed(ex);
        }

        return await state.Task;

        async ValueTask<DurableJobRunResult> LongPollGetJobStatusAsync(
            (string JobId, long ExecutionGeneration, int DequeueCount) key,
            IJobRunContext context,
            JobAttemptState state,
            CancellationToken attemptCancellationToken)
        {
            if (!state.Task.IsCompleted)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(attemptCancellationToken);
                var longPollDuration = TimeSpan.FromTicks(Math.Min(_shared.MessagingOptions.ResponseTimeout.Divide(2).Ticks, _shared.Options.JobStatusPollInterval.Ticks));
                await Task.WhenAny(Task.Delay(longPollDuration, _shared.TimeProvider, cts.Token), state.Task);
                cts.Cancel();

                if (!state.Task.IsCompleted)
                {
                    return DurableJobRunResult.InProgress(_shared.Options.JobStatusPollInterval);
                }
            }

            if (state.Task.IsFaulted)
            {
                RemoveJobAttempt(key, state);
                var ex = state.Task.Exception!.InnerException ?? state.Task.Exception;
                LogErrorExecutingDurableJob(_shared.Logger, ex, context.Job.Id, _grain.GrainId);
                return DurableJobRunResult.Failed(ex);
            }

            if (state.Task.IsCanceled)
            {
                RemoveJobAttempt(key, state);
                return await state.Task;
            }

            return GetSuccessfulResult(key, state, await state.Task);
        }
    }

    private DurableJobRunResult GetSuccessfulResult(
        (string JobId, long ExecutionGeneration, int DequeueCount) key,
        JobAttemptState state,
        DurableJobRunResult result)
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
            RemoveJobAttempt(key, state);
        }

        return result;
    }

    private void RemoveJobAttempt((string JobId, long ExecutionGeneration, int DequeueCount) key, JobAttemptState state)
    {
        if (_jobAttempts.TryGetValue(key, out var current) && ReferenceEquals(current, state))
        {
            _jobAttempts.Remove(key);
        }
    }

    private static (string JobId, long ExecutionGeneration, int DequeueCount) GetExecutionKey(IJobRunContext context)
        => (context.Job.Id, context.Job.ExecutionGeneration, context.DequeueCount);

    internal sealed class TestAccessor(DurableJobReceiverExtension extension)
    {
        public Task<DurableJobRunResult>? GetAttemptTask(IJobRunContext context) =>
            extension._jobAttempts.TryGetValue(GetExecutionKey(context), out var state) ? state.Task : null;
    }

    private sealed class JobAttemptState(Task<DurableJobRunResult> task)
    {
        public Task<DurableJobRunResult> Task { get; } = task;

        public bool PollRequested;

        public long PollTimestamp;

        public TimeSpan PollAfterDelay;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Error executing durable job {JobId} on grain {GrainId}")]
    private static partial void LogErrorExecutingDurableJob(ILogger logger, Exception exception, string jobId, GrainId grainId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Grain {GrainId} does not implement IDurableJobHandler")]
    private static partial void LogGrainDoesNotImplementHandler(ILogger logger, GrainId grainId);
}

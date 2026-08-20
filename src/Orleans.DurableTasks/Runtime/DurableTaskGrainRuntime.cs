#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.DurableJobs;
using Orleans.DurableTasks;
using Orleans.Journaling;

namespace Orleans.Runtime.DurableTasks;

internal sealed partial class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    DurableTaskGrainRuntimeShared shared,
    IEnumerable<IDurableTaskMessageTransport> messageTransports,
    IJournaledStateManager stateManager) :
    IDurableTaskGrainRuntime,
    IDurableTaskGrainExtension,
    IDurableJobFeatureHandler,
    IJournaledStateObserver
{
    private readonly Dictionary<TaskId, GrainDurableExecutionContext> _executionContexts = [];
    private readonly Dictionary<TaskId, Task> _runningRequests = [];
    private readonly object _runningRequestsLock = new();
    private readonly Dictionary<TaskId, IScheduledTaskHandle> _taskHandles = [];
    private readonly Dictionary<TaskId, IDurableTaskRequest> _pendingStarts = [];
    private readonly Dictionary<TaskId, IDurableTaskRequest> _committingStarts = [];
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;
    private readonly IDurableTaskMessageTransport? _messageTransport = messageTransports.SingleOrDefault();
    private readonly IJournaledStateManager _stateManager = stateManager;
    private readonly CancellationTokenSource _deactivationCts = new();
    private Task? _stopTask;
    private volatile bool _stopping;

    private GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

    public DateTimeOffset UtcNow => _shared.TimeProvider.GetUtcNow();

    public async ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (_storage.TryGetTask(decisionId, out var decision)
            && decision.Result is { IsCompleted: true } recorded)
        {
            return recorded.GetResult<TaskId>();
        }

        while (true)
        {
            foreach (var candidate in candidates)
            {
                var response = await GetScheduledTaskHandle(candidate).PollAsync(
                    new PollingOptions { PollTimeout = TimeSpan.Zero },
                    cancellationToken);
                if (!response.IsCompleted)
                {
                    continue;
                }

                decision = _storage.GetOrCreateTask(decisionId, request: null);
                _storage.SetResponse(decisionId, decision, DurableTaskResponse.FromResult(candidate));
                await _storage.WriteAsync(cancellationToken);
                return candidate;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), _shared.TimeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <returns>The new execution context.</returns>
    private GrainDurableExecutionContext CreateExecutionContext(TaskId taskId) =>
        _executionContexts[taskId] = new(taskId, this, _deactivationCts.Token);

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out GrainDurableExecutionContext? executionContext) => _executionContexts.TryGetValue(taskId, out executionContext);

    private bool TryRegisterCompletionDestination(TaskId taskId, IDurableTaskState state, GrainId destination)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (destination.IsDefault || destination.IsClient() || state.CompletionDestinations.Contains(destination))
        {
            return false;
        }

        _storage.AddCompletionDestination(taskId, state, destination);
        return true;
    }

    public async ValueTask<DurableTaskResponse> ScheduleRemoteAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        cancellationToken.ThrowIfCancellationRequested();
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        var context = request.Context ?? throw new InvalidOperationException("The durable task request has no context.");
        context.CallerId = GrainId;
        context.SupportsDurableCompletion = true;
        transport.SendInvocation(GrainId, context.TargetId, taskId, request);
        await _storage.WriteAsync(cancellationToken);
        return DurableTaskResponse.Pending;
    }

    public async ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        transport.SendCancellation(GrainId, target, taskId);
        await _storage.WriteAsync(cancellationToken);
    }

    internal async ValueTask CancelScheduledTaskAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state) || state.Result is { IsCompleted: true })
        {
            return;
        }

        _storage.RequestCancellation(taskId, state);
        await SetResponseAsync(
            taskId,
            DurableTaskResponse.FromException(new OperationCanceledException()),
            cancellationToken);
    }

    public async ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        var state = _storage.GetOrCreateTask(taskId, request: null);
        if (state.Result is { IsCompleted: true } completed)
        {
            return completed;
        }

        if (state.DueTime is { } existingDueTime)
        {
            if (existingDueTime != dueTime)
            {
                throw new InvalidOperationException(
                    $"Durable delay '{taskId}' was already scheduled for '{existingDueTime:O}', not '{dueTime:O}'.");
            }

            return DurableTaskResponse.Pending;
        }

        var generation = checked(state.ResumeGeneration + 1);
        _storage.SetDelay(taskId, state, dueTime, generation);
        await transport.ScheduleResumeAsync(GrainId, taskId, generation, dueTime, cancellationToken);
        await _storage.WriteAsync(cancellationToken);
        return DurableTaskResponse.Pending;
    }

    public async ValueTask<DurableJobRunResult> ExecuteJobAsync(
        IJobRunContext context,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (context.Job.Metadata is not { } metadata
            || !metadata.TryGetValue(DurableTaskMessageTransport.ResumeTaskIdMetadata, out var taskIdText)
            || !TaskId.TryParse(taskIdText, out var taskId)
            || !metadata.TryGetValue(DurableTaskMessageTransport.ResumeGenerationMetadata, out var generationText)
            || !long.TryParse(
                generationText,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var generation))
        {
            return DurableJobRunResult.Completed;
        }

        if (!_storage.TryGetTask(taskId, out var state))
        {
            return _stateManager.PendingWriteByteCount > 0
                ? DurableJobRunResult.RetryAt(UtcNow + TimeSpan.FromMilliseconds(10))
                : DurableJobRunResult.Completed;
        }

        if (state.Result is { IsCompleted: true })
        {
            return DurableJobRunResult.Completed;
        }

        if (state.CancellationRequestedAt.HasValue)
        {
            await SetResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken);
            return DurableJobRunResult.Completed;
        }

        if (state.ResumeGeneration != generation)
        {
            return DurableJobRunResult.Completed;
        }

        if (state.DueTime is { } dueTime && dueTime > UtcNow)
        {
            return DurableJobRunResult.RetryAt(dueTime);
        }

        await SetResponseAsync(taskId, DurableTaskResponse.Completed, cancellationToken);
        return DurableJobRunResult.Completed;
    }

    internal void AcceptResponse(TaskId taskId, DurableTaskResponse response)
    {
        ThrowIfStopping();
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can be accepted.");
        }

        if (!_storage.TryGetTask(taskId, out var state))
        {
            state = _storage.GetOrCreateTask(taskId, request: null);
        }

        var winningResponse = state.Result is { IsCompleted: true } terminal ? terminal : response;
        if (state.Result is not { IsCompleted: true })
        {
            _storage.SetResponse(taskId, state, response);
        }

        if (_taskHandles.TryGetValue(taskId, out var handle) && handle is TaskHandle localHandle)
        {
            localHandle.TrySetResponse(winningResponse);
        }
    }

    internal async ValueTask AcceptResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        GrainId target,
        CancellationToken cancellationToken,
        bool persist = true)
    {
        ThrowIfStopping();
        AcceptResponse(taskId, response);
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        transport.SendCompletionAck(GrainId, target, taskId);
        if (persist)
        {
            await _storage.WriteAsync(cancellationToken);
        }
    }

    internal async ValueTask AcknowledgeCompletionAsync(
        TaskId taskId,
        GrainId destination,
        CancellationToken cancellationToken,
        bool persist = true)
    {
        ThrowIfStopping();
        if (!_storage.TryGetTask(taskId, out var state)
            || !state.CompletionDestinations.Contains(destination))
        {
            return;
        }

        _storage.RemoveCompletionDestination(taskId, state, destination);
        if (persist)
        {
            await _storage.WriteAsync(cancellationToken);
        }
        PruneCompletedTasks();
    }

    internal async Task ResumePendingTasksAsync(CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        foreach (var (taskId, state) in _storage.Tasks.ToList())
        {
            if (state.Result is { IsCompleted: true })
            {
                continue;
            }

            if (state.CancellationRequestedAt.HasValue)
            {
                await SetResponseAsync(taskId, DurableTaskResponse.Canceled, cancellationToken);
                continue;
            }

            if (state.DueTime is { } dueTime && state.ResumeGeneration > 0)
            {
                var transport = _messageTransport ?? throw new InvalidOperationException(
                    "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
                await transport.ScheduleResumeAsync(
                    GrainId,
                    taskId,
                    state.ResumeGeneration,
                    dueTime,
                    cancellationToken);
                continue;
            }

            if (state.Request is null
                || IsRequestRunning(taskId))
            {
                continue;
            }

            var executionContext = CreateExecutionContext(taskId);
            var handle = new TaskHandle(taskId, this) { IsRunning = true };
            _taskHandles[taskId] = handle;
            state.Request.SetTarget(_shared.GrainContextAccessor.GrainContext);
            StartInvocation(taskId, static request => request.CreateTask(), state.Request, executionContext);
        }
    }

    /// <summary>
    /// Durably schedules a request for invocation against this instance.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A <see cref="DurableTaskResponse"/> indicating the status of the request. A response of type <see cref="PendingDurableTaskResponse"/> indicates that the caller can call this method again to poll for completion.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    ValueTask<DurableTaskResponse> IDurableTaskServer.ScheduleAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken) =>
        ScheduleAsyncCore(taskId, request, persist: true, cancellationToken);

    internal ValueTask<DurableTaskResponse> ScheduleFromInboxAsync(
        TaskId taskId,
        IDurableTaskRequest request,
        CancellationToken cancellationToken) =>
        ScheduleAsyncCore(taskId, request, persist: false, cancellationToken);

    private async ValueTask<DurableTaskResponse> ScheduleAsyncCore(
        TaskId taskId,
        IDurableTaskRequest request,
        bool persist,
        CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }

        var fingerprint = IDurableTaskRequest.GetFingerprint(request, _shared.Serializer);
        if (_storage.TryGetTask(taskId, out var identifiedState))
        {
            if (identifiedState.RequestFingerprint is { } existingFingerprint
                && !string.Equals(existingFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' is already associated with a different request.");
            }

            if (identifiedState.RequestFingerprint is null)
            {
                if (identifiedState.Request is { } existingRequest
                    && !string.Equals(
                        IDurableTaskRequest.GetFingerprint(existingRequest, _shared.Serializer),
                        fingerprint,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Durable task '{taskId}' is already associated with a different request.");
                }

                _storage.SetRequestFingerprint(taskId, identifiedState, fingerprint);
            }

            if (identifiedState.TombstonedAt.HasValue)
            {
                throw new InvalidOperationException(
                    $"Durable task '{taskId}' completed and its retained result has expired.");
            }
        }

        if (_storage.TryGetTask(taskId, out var existingState)
            && existingState.CancellationRequestedAt.HasValue
            && existingState.Result is not { IsCompleted: true })
        {
            _storage.SetRequest(taskId, existingState, request);
            if (requestContext.SupportsDurableCompletion)
            {
                TryRegisterCompletionDestination(taskId, existingState, requestContext.CallerId);
            }
            var canceled = DurableTaskResponse.FromException(new OperationCanceledException());
            await SetResponseAsync(taskId, canceled, cancellationToken, persist);
            return canceled;
        }

        // Check if the task is already running.
        if (TryGetScheduledTaskHandle(taskId, out var handle))
        {
            // If it is and it's completed, return the result immediately.
            var response = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
            if (response.IsCompleted)
            {
                if (persist)
                {
                    await _storage.WriteAsync(cancellationToken);
                }
                return response;
            }

            // Register a durable completion destination for grain callers.
            if (_storage.TryGetTask(taskId, out var state)
                && requestContext.SupportsDurableCompletion
                && TryRegisterCompletionDestination(taskId, state, requestContext.CallerId))
            {
                if (persist)
                {
                    await _storage.WriteAsync(cancellationToken);
                }
                return DurableTaskResponse.Subscribed;
            }

            if (persist)
            {
                await _storage.WriteAsync(cancellationToken);
            }
            return DurableTaskResponse.Pending;
        }
        else
        {
            // Create the task state and register the caller if they are addressable.
            var state = _storage.GetOrCreateTask(taskId, request);
            if (state.RequestFingerprint is null)
            {
                _storage.SetRequestFingerprint(taskId, state, fingerprint);
            }
            if (state.Request is null)
            {
                _storage.SetRequest(taskId, state, request);
            }

            var subscribed = requestContext.SupportsDurableCompletion
                && TryRegisterCompletionDestination(taskId, state, requestContext.CallerId);

            // If the task was already scheduled, return a response immediately.
            if (state.Result is { } response && response.IsCompleted)
            {
                if (persist)
                {
                    await _storage.WriteAsync(cancellationToken);
                }
                return response;
            }

            // Persist the task state before invoking the task.
            // Note that if we intercept all outgoing calls to other durable tasks, then we do not need to do this here.
            // Instead, we can defer it until either the task completes or an outgoing call is made, since we can guarantee
            // no visible side-effects.
            // If the user does the 'wrong' thing and calls a non-durable task from their code, then that could expose an externality.
            if (persist)
            {
                await _storage.WriteAsync(cancellationToken);
                StartRequest(taskId, request);
            }
            else
            {
                _pendingStarts[taskId] = request;
            }

            return subscribed ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending;
        }
    }

    private void StartRequest(TaskId taskId, IDurableTaskRequest request)
    {
        if (_stopping || IsRequestRunning(taskId))
        {
            return;
        }

        var executionContext = CreateExecutionContext(taskId);
        var handle = new TaskHandle(taskId, this) { IsRunning = true };
        _taskHandles[taskId] = handle;
        request.SetTarget(_shared.GrainContextAccessor.GrainContext);
        StartInvocation(taskId, static value => value.CreateTask(), request, executionContext);
    }

    public void OnWriteStarted()
    {
        _committingStarts.Clear();
        foreach (var entry in _pendingStarts)
        {
            _committingStarts[entry.Key] = entry.Value;
        }
    }

    public void OnWriteCompleted()
    {
        foreach (var (taskId, request) in _committingStarts)
        {
            _pendingStarts.Remove(taskId);
            StartRequest(taskId, request);
        }

        _committingStarts.Clear();
    }

    public void OnRecoveryCompleted()
    {
        _pendingStarts.Clear();
        _committingStarts.Clear();
    }

    public async ValueTask<IScheduledTaskHandle> ScheduleChildAsync(TaskId taskId, DurableTask durableTask, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        var state = _storage.GetOrCreateTask(taskId, null);
        if (durableTask is IDurableTaskRequest remoteRequest)
        {
            var target = remoteRequest.Context?.TargetId
                ?? throw new InvalidOperationException("The durable task request has no target.");
            var fingerprint = IDurableTaskRequest.GetFingerprint(remoteRequest, _shared.Serializer);
            if ((!state.RemoteTarget.IsDefault && state.RemoteTarget != target)
                || (state.RemoteRequestFingerprint is { } existing
                    && !string.Equals(existing, fingerprint, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Durable child task '{taskId}' is already associated with a different request.");
            }

            if (state.RemoteTarget.IsDefault || state.RemoteRequestFingerprint is null)
            {
                _storage.SetRemoteRequest(taskId, state, target, fingerprint);
            }
        }

        // If the task is currently running, return the existing handle after validating replay identity.
        if (TryGetScheduledTaskHandle(taskId, out var handle) && (handle is not TaskHandle localHandle || localHandle.IsRunning))
        {
            return handle;
        }

        // If the task is schedulable, schedule it.
        if (durableTask is ISchedulableTask schedulableTask)
        {
            if (_messageTransport is not null)
            {
                handle = durableTask is IDurableTaskRequest messageRequest
                    ? new TaskHandle(taskId, this, messageRequest.Context!.TargetId) { IsRunning = true }
                    : new TaskHandle(taskId, this) { IsRunning = true };
                _taskHandles[taskId] = handle;
            }

            var schedulingResponse = await schedulableTask.ScheduleAsync(taskId, cancellationToken);
            if (schedulingResponse.IsCompleted)
            {
                _storage.SetResponse(taskId, state, schedulingResponse);
                await _storage.WriteAsync(cancellationToken);
                if (handle is TaskHandle completedHandle)
                {
                    completedHandle.TrySetResponse(schedulingResponse);
                }
                return new CompletedTaskHandle(taskId, schedulingResponse);
            }

            if (state.Result is { IsCompleted: true } completedResponse)
            {
                return new CompletedTaskHandle(taskId, completedResponse);
            }

            if (_messageTransport is null)
            {
                handle = schedulableTask.GetHandle(taskId);
                _taskHandles[taskId] = handle;
            }

            await _storage.WriteAsync(cancellationToken);
            return handle!;
        }

        // Otherwise, the task must be a local method invocation, so create an execution context for it and execute it.
        var executionContext = CreateExecutionContext(taskId);
        handle = new TaskHandle(taskId, this) { IsRunning = true };
        _taskHandles[taskId] = handle;
        StartInvocation(taskId, static task => task, durableTask, executionContext);
        return handle;
    }

    private void StartInvocation<TState>(
        TaskId taskId,
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableExecutionContext context)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = Invoke(createTask, state, context, start.Task);
        lock (_runningRequestsLock)
        {
            _runningRequests.Add(taskId, invocation);
        }
        start.SetResult();
    }

    private async Task Invoke<TState>(
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableExecutionContext context,
        Task start)
    {
        await start;
        DurableTaskResponse response;
        try
        {
            response = await DurableTaskRuntimeHelper.RunAsync(createTask(state), context);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
            {
                _shared.Logger.LogWarning(exception, "{Id} error invoking durable task '{DurableTask}'.", GrainId, createTask);
            }

            response = DurableTaskResponse.FromException(exception);
        }

        try
        {
            if (response.IsCompleted && !_stopping)
            {
                await SetResponseAsync(context.TaskId, response, CancellationToken.None);
            }
        }
        finally
        {
            lock (_runningRequestsLock)
            {
                _runningRequests.Remove(context.TaskId);
            }
            _executionContexts.Remove(context.TaskId);
        }
    }

    private async Task SetResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken,
        bool persist = true)
    {
        if (!response.IsCompleted)
        {
            throw new InvalidOperationException("Only terminal durable task responses can complete a task.");
        }

        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} task {TaskId} completed with result '{Result}'.", GrainId, taskId, response);
        }

        // Only update the result if an existing result has not been set. If this were to overwrite an already-persisted result,
        // that could cause the result to appear to change after it has already been observed.
        // This condition guards against the case where a scheduling call fails after the response has already been received via an OnResponse callback,
        // which could occur due to a recovery retry or concurrency (multiple clients scheduling the same workflow).
        if (!_storage.TryGetTask(taskId, out var state))
        {
            throw new InvalidOperationException($"Cannot complete unknown task '{taskId}'.");
        }

        var winningResponse = state.Result is { IsCompleted: true } terminal ? terminal : response;
        if (state.Result is not { IsCompleted: true })
        {
            if (state is DurableTaskState durableState)
            {
                durableState.MigrateLegacyObservers();
            }

            if (state.CompletionDestinations.Count > 0)
            {
                var transport = _messageTransport ?? throw new InvalidOperationException(
                    "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
                foreach (var destination in state.CompletionDestinations)
                {
                    transport.SendCompletion(GrainId, destination, taskId, response);
                }

            }

            _storage.SetResponse(taskId, state, response);
            if (persist)
            {
                await _storage.WriteAsync(cancellationToken);
            }
        }

        if (_taskHandles.TryGetValue(taskId, out var handle))
        {
            if (handle is TaskHandle localHandle)
            {
                localHandle.TrySetResponse(winningResponse);
            }
        }

        PruneCompletedTasks();
    }

    private bool PruneCompletedTasks()
    {
        // Prune all tasks which:
        // * Have a response
        // * Have no remaining clients to notify
        // * Have no parents waiting on them within this context
        // * Have been completed for more than a configured period of time
        var allTasks = _storage.Tasks.ToDictionary(static task => task.Id, static task => task.State);
        HashSet<TaskId>? completedTaskIds = default;
        Dictionary<TaskId, HashSet<TaskId>>? waitingOnParent = default;
        var now = _shared.TimeProvider.GetUtcNow();
        foreach (var (taskId, state) in allTasks)
        {
            if (state.Result is not { IsCompleted: true })
            {
                // The task is incomplete.
                continue;
            }

            if (state.CompletionDestinations.Count > 0)
            {
                // There are still unacknowledged clients.
                continue;
            }

            if (state.CompletedAt is not { } completedAt || now.Subtract(completedAt) < _shared.Options.ResultRetentionPeriod)
            {
                // The task is being retained for at least the specified period of time.
                continue;
            }

            if (taskId.Parent() is { } parent && parent != TaskId.None && allTasks.ContainsKey(parent))
            {
                // There is a local parent task which this task is waiting on, and that is the last thing keeping this task alive.
                waitingOnParent ??= [];
                ref var waiters = ref CollectionsMarshal.GetValueRefOrAddDefault(waitingOnParent, parent, out var exists);
                waiters ??= [];
                waiters.Add(taskId);
                continue;
            }

            completedTaskIds ??= [];
            completedTaskIds.Add(taskId);
        }

        if (completedTaskIds is not null)
        {
            foreach (var taskId in completedTaskIds)
            {
                PruneTaskTree(taskId);
            }
        }

        return completedTaskIds is not null;

        void PruneTaskTree(TaskId taskId)
        {
            if (waitingOnParent is not null && waitingOnParent.TryGetValue(taskId, out var childTaskIds))
            {
                foreach (var childTaskId in childTaskIds)
                {
                    PruneTaskTree(childTaskId);
                }
            }

            if (_shared.Logger.IsEnabled(LogLevel.Trace))
            {
                _shared.Logger.LogTrace("{Id} pruning completed task {TaskId}", GrainId, taskId);
            }

            var completedState = allTasks[taskId];
            if (completedState.RequestFingerprint is not null)
            {
                _storage.CreateTombstone(taskId, completedState);
            }
            else
            {
                _storage.RemoveTask(taskId);
            }

            _executionContexts.Remove(taskId);
            _taskHandles.Remove(taskId);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<DurableTaskResponse> SubscribeOrPollAsync(TaskId taskId, SubscribeOrPollOptions options, CancellationToken cancellationToken)
    {
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} received polling request for task {TaskId}", GrainId, taskId);
        }

        var handle = GetScheduledTaskHandle(taskId);
        var response = await handle.PollAsync(new PollingOptions { PollTimeout = options.PollTimeout }, cancellationToken);
        if (response.IsCompleted)
        {
            return response;
        }

        return DurableTaskResponse.Pending;
    }

    async IAsyncEnumerable<(TaskId TaskId, DurableTaskDiagnosticState State)> IDurableTaskGrainExtension.GetTasksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;

        foreach (var (taskId, taskState) in _storage.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = GetDiagnosticState(taskState);

            yield return (taskId, state);
        }

        static DurableTaskDiagnosticState GetDiagnosticState(IDurableTaskState taskState)
        {
            return new DurableTaskDiagnosticState
            {
                CompletedAt = taskState.CompletedAt,
                CreatedAt = taskState.CreatedAt,
                Response = taskState.Result?.ToString(),
                Request = taskState.Request?.ToMethodCallString(),
                Status = taskState.Result switch
                {
                    { } response when response.Exception is null => "Completed",
                    { } => "Faulted",
                    null => "Pending",
                },
                Waiters = taskState.CompletionDestinations.Select(static destination => destination.ToString()).ToList(),
            };
        }
    }

    async IAsyncEnumerable<TaskId> IDurableTaskGrainExtension.GetRunningTasksAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        List<TaskId> running;
        lock (_runningRequestsLock)
        {
            running = _runningRequests.Keys.ToList();
        }

        foreach (var taskId in running)
        {
            yield return taskId;
        }
    }

    public async ValueTask SignalCancellationAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        ThrowIfStopping();
        if (taskId.IsDefault)
        {
            throw new ArgumentException("Invalid TaskId.", nameof(taskId));
        }

        if (!_storage.TryGetTask(taskId, out var taskState))
        {
            taskState = _storage.GetOrCreateTask(taskId, request: null);
            _storage.RequestCancellation(taskId, taskState);
            await _storage.WriteAsync(cancellationToken);
            return;
        }

        List<GrainDurableExecutionContext> canceledContexts = [];
        List<IScheduledTaskHandle> canceledHandles = [];
        if (RequestCancellationCore(taskId, taskState, canceledContexts, canceledHandles))
        {
            // Something changed, write state.
            await _storage.WriteAsync(cancellationToken);
        }

        // Cancel all tasks that we found.
        var tasks = new List<Task>(canceledContexts.Count);
        foreach (var context in canceledContexts)
        {
            tasks.Add(DurableTaskRuntimeHelper.RequestCancellationAsync(context, cancellationToken));
        }

        foreach (var handle in canceledHandles)
        {
            tasks.Add(handle.CancelAsync(cancellationToken).AsTask());
        }

        await Task.WhenAll(tasks);

        bool RequestCancellationCore(TaskId taskId, IDurableTaskState taskState, List<GrainDurableExecutionContext> canceledContexts, List<IScheduledTaskHandle> canceledHandles)
        {
            if (taskState.CompletedAt.HasValue)
            {
                // If the task has completed then all child tasks have completed.
                return false;
            }

            if (taskState.CancellationRequestedAt.HasValue)
            {
                // Cancellation has already been requested.
                return false;
            }

            // Find all immediate children of the task and start canceling them.
            // TODO: It may be more efficient to get all descendants and to enumerate them in descendant-first order.
            foreach (var (childTaskId, childTaskState) in _storage.GetChildren(taskId))
            {
                Debug.Assert(taskId.IsParentOf(childTaskId));
                _ = RequestCancellationCore(childTaskId, childTaskState, canceledContexts, canceledHandles);
            }

            _storage.RequestCancellation(taskId, taskState);
            if (TryGetExecutionContext(taskId, out var context))
            {
                canceledContexts.Add(context);
            }
            else if (TryGetScheduledTaskHandle(taskId, out var handle))
            {
                canceledHandles.Add(handle);
            }

            return true;
        }
    }

    async ValueTask IDurableTaskServer.CancelAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        await SignalCancellationAsync(taskId, cancellationToken);
    }

    private bool TryGetScheduledTaskHandle(TaskId taskId, [NotNullWhen(true)] out IScheduledTaskHandle? handle)
    {
        if (_taskHandles.TryGetValue(taskId, out handle))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var taskState))
        {
            // Rehydrate the task handle.
            if (taskState.Result is { } response)
            {
                Debug.Assert(response.IsCompleted);
                handle = new CompletedTaskHandle(taskId, response);
                return true;
            }
            else
            {
                // Create a new handle for the task.
                handle = new TaskHandle(taskId, this, taskState.RemoteTarget);
                _taskHandles.Add(taskId, handle);
                return true;
            }
        }

        return false;
    }

    public IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId)
    {
        if (!TryGetScheduledTaskHandle(taskId, out var handle))
        {
            throw new KeyNotFoundException($"A task with the identifier '{taskId}' was not found.");
        }

        return handle;
    }

    internal Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        return _stopTask ??= StopCoreAsync(cancellationToken);
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        await _deactivationCts.CancelAsync();
        var shutdownResponse = DurableTaskResponse.FromCanceled(
            new OperationCanceledException("The grain activation is stopping.", _deactivationCts.Token));
        foreach (var handle in _taskHandles.Values)
        {
            if (handle is TaskHandle localHandle)
            {
                localHandle.TrySetResponse(shutdownResponse);
            }
        }

        while (true)
        {
            Task[] running;
            lock (_runningRequestsLock)
            {
                if (_runningRequests.Count == 0)
                {
                    return;
                }

                running = _runningRequests.Values.ToArray();
            }

            // Abandoning the drain would allow a replacement activation to overlap this one.
            // Adapter-controlled waits observe the lifecycle token. Arbitrary user code can ignore it, so
            // deactivation deliberately waits for every execution before replacement replay can begin.
            await Task.WhenAll(running);
        }
    }

    private bool IsRequestRunning(TaskId taskId)
    {
        lock (_runningRequestsLock)
        {
            return _runningRequests.ContainsKey(taskId);
        }
    }

    private void ThrowIfStopping()
    {
        if (_stopping)
        {
            throw new OperationCanceledException(
                "The durable task runtime is stopping and cannot accept new work.",
                _deactivationCts.Token);
        }
    }
}

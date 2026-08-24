#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.DurableTasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed partial class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    DurableTaskGrainRuntimeShared shared,
    IEnumerable<IDurableTaskMessageTransport> messageTransports) : IDurableTaskGrainRuntime, IDurableTaskGrainExtension
{
    private readonly Dictionary<TaskId, GrainDurableExecutionContext> _executionContexts = [];
    private readonly Dictionary<TaskId, Task> _runningRequests = [];
    private readonly Dictionary<TaskId, IScheduledTaskHandle> _taskHandles = [];
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;
    private readonly IDurableTaskMessageTransport? _messageTransport = messageTransports.SingleOrDefault();

    private readonly CancellationTokenSource _deactivationCts = new();
    private readonly SemaphoreSlim _stopLock = new(1, 1);
    private int _admissionStopped;
    private int _runtimeStateDisposed;

    private GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

    private void EnsureAcceptingRequests()
    {
        if (Volatile.Read(ref _admissionStopped) != 0)
        {
            throw new InvalidOperationException("The durable task runtime is deactivating and is not accepting new work.");
        }
    }

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <returns>The new execution context.</returns>
    private GrainDurableExecutionContext CreateExecutionContext(TaskId taskId) => _executionContexts[taskId] = new(taskId, this);

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
        if (destination.IsDefault || state.CompletionDestinations.Contains(destination))
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
        EnsureAcceptingRequests();
        cancellationToken.ThrowIfCancellationRequested();
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        var context = request.Context ?? throw new InvalidOperationException("The durable task request has no context.");
        context.CallerId = GrainId;
        transport.SendInvocation(GrainId, context.TargetId, taskId, request);
        await transport.CommitAsync(cancellationToken);
        return DurableTaskResponse.Pending;
    }

    public async ValueTask CancelRemoteAsync(TaskId taskId, GrainId target, CancellationToken cancellationToken)
    {
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        if (_storage.TryGetTask(taskId, out var state)
            && state.Result is not { IsCompleted: true }
            && state is DurableTaskState durableState)
        {
            durableState.PendingCancellationDestination = target;
            _storage.RequestCancellation(taskId, state);
        }

        transport.SendCancellation(GrainId, target, taskId);
        await transport.CommitAsync(cancellationToken);
    }

    internal async ValueTask CancelScheduledTaskAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        if (!_storage.TryGetTask(taskId, out var state) || state.Result is { IsCompleted: true })
        {
            return;
        }

        await SignalCancellationAsync(taskId, cancellationToken, cancelRootHandle: false);
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
        EnsureAcceptingRequests();
        var transport = _messageTransport ?? throw new InvalidOperationException(
            "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
        await transport.ScheduleResumeAsync(GrainId, taskId, dueTime, cancellationToken);
        return DurableTaskResponse.Pending;
    }

    internal void AcceptResponse(TaskId taskId, DurableTaskResponse response)
    {
        if (!_storage.TryGetTask(taskId, out var state))
        {
            state = _storage.GetOrCreateTask(taskId, request: null);
        }

        if (state.Result is not { IsCompleted: true })
        {
            _storage.SetResponse(taskId, state, response);
        }

        if (_taskHandles.TryGetValue(taskId, out var handle) && handle is TaskHandle localHandle)
        {
            localHandle.TrySetResponse(response);
        }
    }

    internal async Task ResumePendingTasksAsync(CancellationToken cancellationToken)
    {
        EnsureAcceptingRequests();
        var pendingCancellations = _storage.Tasks
            .Where(static task => task.State is DurableTaskState { PendingCancellationDestination.IsDefault: false })
            .Select(static task => (task.Id, ((DurableTaskState)task.State).PendingCancellationDestination))
            .ToList();
        if (pendingCancellations.Count > 0)
        {
            var transport = _messageTransport ?? throw new InvalidOperationException(
                "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
            foreach (var (taskId, destination) in pendingCancellations)
            {
                transport.SendCancellation(GrainId, destination, taskId);
            }

            await transport.CommitAsync(cancellationToken);
        }

        foreach (var (taskId, state) in _storage.Tasks.ToList())
        {
            if (state.Result is { IsCompleted: true }
                || state.Request is null
                || _runningRequests.ContainsKey(taskId))
            {
                continue;
            }

            if (state.CancellationRequestedAt.HasValue)
            {
                if (state is DurableTaskState { PendingCancellationDestination.IsDefault: false })
                {
                    continue;
                }

                await SetResponseAsync(taskId, DurableTaskResponse.FromException(new OperationCanceledException()), cancellationToken);
                continue;
            }

            var executionContext = CreateExecutionContext(taskId);
            var handle = new TaskHandle(taskId, this) { IsRunning = true };
            _taskHandles[taskId] = handle;
            state.Request.SetTarget(_shared.GrainContextAccessor.GrainContext);
            _runningRequests[taskId] = Invoke(
                static request => request.CreateTask(),
                state.Request,
                executionContext,
                restorePersistedRequestContext: true);
        }
    }

    /// <summary>
    /// Durably schedules a request for invocation against this instance.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A <see cref="DurableTaskResponse"/> indicating the status of the request. A response of type <see cref="PendingDurableTaskResponse"/> indicates that the caller can call this method again to poll for completion.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    async ValueTask<DurableTaskResponse> IDurableTaskServer.ScheduleAsync(TaskId taskId, IDurableTaskRequest request, CancellationToken cancellationToken)
    {
        EnsureAcceptingRequests();
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }
        requestContext.Validate();

        if (_storage.TryGetTask(taskId, out var persistedState)
            && persistedState.Request is { } persistedRequest
            && !IDurableTaskRequest.AreRequestsEquivalent(persistedRequest, request, _shared.Serializer))
        {
            throw new InvalidOperationException(
                $"Task id '{taskId}' is already associated with a different durable task request.");
        }

        if (_storage.TryGetTask(taskId, out var existingState)
            && existingState.CancellationRequestedAt.HasValue
            && existingState.Result is not { IsCompleted: true })
        {
            if (existingState.Request is null)
            {
                _storage.SetRequest(taskId, existingState, request);
            }

            TryRegisterCompletionDestination(taskId, existingState, requestContext.CallerId);
            var canceled = DurableTaskResponse.FromException(new OperationCanceledException());
            await SetResponseAsync(taskId, canceled, cancellationToken);
            return canceled;
        }

        // Check if the task is already running.
        if (TryGetScheduledTaskHandle(taskId, out var handle)
            && (handle is not TaskHandle localHandle || localHandle.IsRunning))
        {
            // If it is and it's completed, return the result immediately.
            var response = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
            if (response.IsCompleted)
            {
                return response;
            }

            // Register a durable completion destination for grain callers.
            if (_storage.TryGetTask(taskId, out var state)
                && TryRegisterCompletionDestination(taskId, state, requestContext.CallerId))
            {
                await _storage.WriteAsync(cancellationToken);
                return DurableTaskResponse.Subscribed;
            }

            return DurableTaskResponse.Pending;
        }
        else
        {
            // Create the task state and register the caller if they are addressable.
            var state = _storage.GetOrCreateTask(taskId, request);
            if (state.Request is null)
            {
                _storage.SetRequest(taskId, state, request);
            }

            var subscribed = TryRegisterCompletionDestination(taskId, state, requestContext.CallerId);

            // If the task was already scheduled, return a response immediately.
            if (state.Result is { } response && response.IsCompleted)
            {
                return response;
            }

            // Reserve the running handle before yielding so that an interleaved duplicate schedule observes
            // the in-progress invocation instead of rehydrating a competing handle from storage.
            var executionContext = CreateExecutionContext(taskId);
            handle = new TaskHandle(taskId, this) { IsRunning = true };
            _taskHandles[taskId] = handle;

            // Persist the task state before invoking the task.
            // Note that if we intercept all outgoing calls to other durable tasks, then we do not need to do this here.
            // Instead, we can defer it until either the task completes or an outgoing call is made, since we can guarantee
            // no visible side-effects.
            // If the user does the 'wrong' thing and calls a non-durable task from their code, then that could expose an externality.
            try
            {
                await _storage.WriteAsync(cancellationToken);
            }
            catch
            {
                _storage.RemoveTask(taskId);
                _executionContexts.Remove(taskId);
                _taskHandles.Remove(taskId);
                throw;
            }

            // Schedule the task with the runtime.
            var requestToInvoke = state.Request ?? request;
            requestToInvoke.SetTarget(_shared.GrainContextAccessor.GrainContext);
            var invocationTask = Invoke(
                static request => request.CreateTask(),
                requestToInvoke,
                executionContext,
                restorePersistedRequestContext: true);
            _runningRequests.Add(taskId, invocationTask);

            return subscribed ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending;
        }
    }

    public async ValueTask<IScheduledTaskHandle> ScheduleChildAsync(TaskId taskId, DurableTask durableTask, CancellationToken cancellationToken)
    {
        EnsureAcceptingRequests();
        if (_shared.Logger.IsEnabled(LogLevel.Trace))
        {
            _shared.Logger.LogTrace("{Id} evaluating task {TaskId}", GrainId, taskId);
        }

        // If the task is currently running, return the existing handle.
        if (TryGetScheduledTaskHandle(taskId, out var handle) && (handle is not TaskHandle localHandle || localHandle.IsRunning))
        {
            return handle;
        }

        var state = _storage.GetOrCreateTask(taskId, null);

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

            if (!schedulableTask.CommitsDurableState)
            {
                await _storage.WriteAsync(cancellationToken);
            }
            return handle!;
        }

        // Otherwise, the task must be a local method invocation, so create an execution context for it and execute it.
        var executionContext = CreateExecutionContext(taskId);
        handle =  new TaskHandle(taskId, this) { IsRunning = true };
        _taskHandles.Add(taskId, handle);
        var invocationTask = Invoke(static task => task, durableTask, executionContext);
        _runningRequests.Add(taskId, invocationTask);
        return handle;
    }

    private async Task Invoke<TState>(
        Func<TState, DurableTask> createTask,
        TState state,
        GrainDurableExecutionContext context,
        bool restorePersistedRequestContext = false)
    {
        DurableTaskResponse response;
        try
        {
            using var requestContextScope = restorePersistedRequestContext
                && state is IDurableTaskRequest { Context: { } request }
                ? request.RestoreRequestContext(_shared.Serializer)
                : null;
            DurableTaskRuntimeHelper.SetCurrentContext(context);
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
            if (Volatile.Read(ref _admissionStopped) == 0
                || response is { IsCompleted: true, Status: not DurableTaskStatus.Canceled }
                || _storage.TryGetTask(context.TaskId, out var taskState) && taskState.CancellationRequestedAt.HasValue)
            {
                await SetResponseAsync(context.TaskId, response, _deactivationCts.Token);
            }
        }
        finally
        {
            _runningRequests.Remove(context.TaskId);
        }
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = Interlocked.Exchange(ref _admissionStopped, 1);
        await _stopLock.WaitAsync(CancellationToken.None);
        try
        {
            if (Volatile.Read(ref _runtimeStateDisposed) != 0)
            {
                return;
            }

            var runningRequests = _runningRequests.Values.ToArray();
            var deactivations = _executionContexts.Values
                .Select(context => context.DeactivateForActivationAsync(cancellationToken))
                .ToArray();
            var terminal = Task.WhenAll(deactivations.Concat(runningRequests));
            Exception? terminalException = null;
            try
            {
                using var timeout = new CancellationTokenSource(_shared.DeactivationDrainTimeout, _shared.TimeProvider);
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    timeout.Token);
                await terminal.WaitAsync(linkedCancellation.Token);
            }
            catch (OperationCanceledException) when (!terminal.IsCompleted)
            {
                _shared.Logger.LogWarning(
                    "Durable task deactivation exceeded the {DrainTimeout} cooperative drain period. "
                    + "Activation teardown will wait until all activation-owned executions are terminal.",
                    _shared.DeactivationDrainTimeout);
                await terminal.ConfigureAwait(true);
            }
            catch (Exception exception) when (terminal.IsCompleted)
            {
                terminalException = exception;
            }

            await _deactivationCts.CancelAsync();
            _deactivationCts.Dispose();
            _runningRequests.Clear();
            _executionContexts.Clear();
            _taskHandles.Clear();
            Volatile.Write(ref _runtimeStateDisposed, 1);

            if (terminalException is not null)
            {
                ExceptionDispatchInfo.Capture(terminalException).Throw();
            }
        }
        finally
        {
            _stopLock.Release();
        }
    }

    private async Task SetResponseAsync(
        TaskId taskId,
        DurableTaskResponse response,
        CancellationToken cancellationToken)
    {
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

        if (state.Result is not { IsCompleted: true })
        {
            if (state is DurableTaskState durableState)
            {
                durableState.MigrateLegacyObservers();
                durableState.PendingCancellationDestination = default;
            }

            var sentCompletion = false;
            if (state.CompletionDestinations.Count > 0)
            {
                var transport = _messageTransport ?? throw new InvalidOperationException(
                    "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
                foreach (var destination in state.CompletionDestinations)
                {
                    transport.SendCompletion(GrainId, destination, taskId, response);
                    sentCompletion = true;
                }

                _storage.ClearCompletionDestinations(taskId, state);
            }

            _storage.SetResponse(taskId, state, response);
            if (sentCompletion)
            {
                await _messageTransport!.CommitAsync(cancellationToken);
            }
            else
            {
                await _storage.WriteAsync(cancellationToken);
            }
        }

        if (_taskHandles.TryGetValue(taskId, out var handle))
        {
            if (handle is TaskHandle localHandle)
            {
                localHandle.TrySetResponse(response);
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

            if (state.CompletedAt is not { } completedAt || now.Subtract(completedAt) < _shared.DefaultCleanupPolicy.CleanupAge)
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
                // Prune all otherwise-completed children.
                if (waitingOnParent is not null && waitingOnParent.TryGetValue(taskId, out var childTaskIds))
                {
                    foreach (var childTaskId in childTaskIds)
                    {
                        if (_shared.Logger.IsEnabled(LogLevel.Trace))
                        {
                            _shared.Logger.LogTrace("{Id} pruning completed child task {TaskId}", GrainId, childTaskId);
                        }

                        _storage.RemoveTask(childTaskId);
                        _executionContexts.Remove(childTaskId);
                        _taskHandles.Remove(childTaskId);
                    }
                }

                // Prune the task.
                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("{Id} pruning completed task {TaskId}", GrainId, taskId);
                }

                _storage.RemoveTask(taskId);
                _executionContexts.Remove(taskId);
                _taskHandles.Remove(taskId);
            }
        }

        return completedTaskIds is not null;
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
        foreach (var task in _runningRequests.ToList())
        {
            yield return task.Key;
        }
    }

    public async ValueTask SignalCancellationAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        await SignalCancellationAsync(taskId, cancellationToken, cancelRootHandle: true);
        if (_storage.TryGetTask(taskId, out var state) && state.Result is not { IsCompleted: true })
        {
            await SetResponseAsync(
                taskId,
                DurableTaskResponse.FromException(new OperationCanceledException()),
                cancellationToken);
        }
    }

    private async ValueTask SignalCancellationAsync(
        TaskId taskId,
        CancellationToken cancellationToken,
        bool cancelRootHandle)
    {
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
        List<(TaskId TaskId, GrainId Target)> cancellationMessages = [];
        if (RequestCancellationCore(taskId, taskState, cancelRootHandle, canceledContexts, canceledHandles, cancellationMessages))
        {
            if (cancellationMessages.Count > 0)
            {
                var transport = _messageTransport ?? throw new InvalidOperationException(
                    "Durable messaging is not configured. Call AddDurableTasks on the silo builder.");
                foreach (var message in cancellationMessages)
                {
                    transport.SendCancellation(GrainId, message.Target, message.TaskId);
                }

                // The task-state mutations and outgoing cancellation envelopes share the journal transaction.
                await transport.CommitAsync(cancellationToken);
            }
            else
            {
                await _storage.WriteAsync(cancellationToken);
            }
        }

        // Cancel all tasks that we found.
        var tasks = new List<Task>(canceledContexts.Count);
        foreach (var context in canceledContexts)
        {
            tasks.Add(DurableTaskRuntimeHelper.CancelAsync(context, cancellationToken));
        }

        foreach (var handle in canceledHandles)
        {
            tasks.Add(handle.CancelAsync(cancellationToken).AsTask());
        }

        await Task.WhenAll(tasks);

        bool RequestCancellationCore(
            TaskId taskId,
            IDurableTaskState taskState,
            bool cancelHandle,
            List<GrainDurableExecutionContext> canceledContexts,
            List<IScheduledTaskHandle> canceledHandles,
            List<(TaskId TaskId, GrainId Target)> cancellationMessages)
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
                _ = RequestCancellationCore(childTaskId, childTaskState, cancelHandle: true, canceledContexts, canceledHandles, cancellationMessages);
            }

            _ = TryGetExecutionContext(taskId, out var context);
            IScheduledTaskHandle? handle = null;
            if (context is null && cancelHandle)
            {
                _ = TryGetScheduledTaskHandle(taskId, out handle);
                if (handle is TaskHandle { RemoteTarget.IsDefault: false } remoteHandle
                    && taskState is DurableTaskState durableState)
                {
                    durableState.PendingCancellationDestination = remoteHandle.RemoteTarget;
                    cancellationMessages.Add((taskId, remoteHandle.RemoteTarget));
                }
            }

            _storage.RequestCancellation(taskId, taskState);
            if (context is not null)
            {
                canceledContexts.Add(context);
            }
            else if (handle is not null)
            {
                if (handle is not TaskHandle { RemoteTarget.IsDefault: false })
                {
                    canceledHandles.Add(handle);
                }
            }

            return true;
        }
    }

    async ValueTask IDurableTaskServer.CancelAsync(TaskId taskId, CancellationToken cancellationToken)
    {
        await SignalCancellationAsync(taskId, cancellationToken);
    }

    internal async ValueTask AcceptCancellationAcknowledgementAsync(
        TaskId taskId,
        GrainId sender,
        DurableTaskResponse response,
        CancellationToken cancellationToken)
    {
        if (!_storage.TryGetTask(taskId, out var state)
            || state is not DurableTaskState durableState
            || durableState.PendingCancellationDestination != sender)
        {
            return;
        }

        durableState.PendingCancellationDestination = default;
        response = state.Result is { IsCompleted: true } completed
            ? completed
            : response;
        _storage.SetResponse(taskId, state, response);
        await _storage.WriteAsync(cancellationToken);
        if (_taskHandles.TryGetValue(taskId, out var handle) && handle is TaskHandle localHandle)
        {
            localHandle.TrySetResponse(response);
        }
    }

    internal DurableTaskResponse GetCancellationAcknowledgementResponse(TaskId taskId)
    {
        if (_storage.TryGetTask(taskId, out var state) && state.Result is { IsCompleted: true } completed)
        {
            return completed;
        }

        throw new InvalidOperationException(
            $"Cannot acknowledge cancellation for task '{taskId}' before its terminal response is durable.");
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
                handle = new TaskHandle(taskId, this);
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
}

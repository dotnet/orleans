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
using Orleans.DurableTasks;

namespace Orleans.Runtime.DurableTasks;

internal sealed partial class DurableTaskGrainRuntime(
    IDurableTaskGrainStorage storage,
    DurableTaskGrainRuntimeShared shared) : IDurableTaskGrainRuntime, IDurableTaskGrainExtension
{
    private readonly Dictionary<TaskId, GrainDurableExecutionContext> _executionContexts = [];
    private readonly Dictionary<TaskId, Task> _runningRequests = [];
    private readonly Dictionary<TaskId, IScheduledTaskHandle> _taskHandles = [];
    private readonly DurableTaskGrainRuntimeShared _shared = shared;
    private readonly IDurableTaskGrainStorage _storage = storage;

    // TODO: Cancel during deactivation.
    // Then drain all tasks.
    private readonly CancellationTokenSource _deactivationCts = new();

    private GrainId GrainId => _shared.GrainContextAccessor.GrainContext.GrainId;

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

    /// <summary>
    /// Gets a reference to the caller if the caller supports durable task notification callbacks.
    /// </summary>
    /// <param name="requestContext">The request context.</param>
    /// <returns>A reference to the caller if the caller supports notifications callbacks, otherwise <see langword="null"/>.</returns>
    private IDurableTaskGrainExtension? GetCallerReferenceOrDefault(DurableTaskRequestContext requestContext)
    {
        var caller = requestContext.CallerId;
        if (caller.IsDefault)
        {
            return null;
        }

        var type = caller.Type;

        // TODO: Consider using (cleaner?) grain manifest lookup instead. Placement can configure manifest (eg, see StatelessWorkerPlacement)
        var placement = _shared.PlacementStrategyResolver.GetPlacementStrategy(type);
        if (placement.IsGrain)
        {
            return _shared.GrainFactory.GetGrain<IDurableTaskGrainExtension>(caller);
        }

        return null;
    }

    private bool TrySubscribeClient(TaskId taskId, IDurableTaskState state, IDurableTaskObserver? client)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (client is not null && (state.Observers is not { } observers || !observers.Contains(client)))
        {
            // Add the client to the persisted task state but do not write state yet: that will be the responsibility of the caller.
            _storage.AddObserver(taskId, state, client);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Called upon completion of a task. The receiver must persist consume the response as the caller may clear task state after this method returns.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="response">The task result.</param>
    /// <returns>A <see cref="ValueTask"/> representing the work performed.</returns>
    ValueTask IDurableTaskObserver.OnResponseAsync(TaskId taskId, DurableTaskResponse response, CancellationToken cancellationToken)
    {
        throw new NotImplementedException("TODO");
    }

    /// <summary>
    /// Durably schedules a request for invocation against this instance.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>A <see cref="DurableTaskResponse"/> indicating the status of the request. A response of type <see cref="PendingDurableTaskResponse"/> indicates that the caller can call this method again to poll for completion.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    async ValueTask<DurableTaskResponse> IDurableTaskServer.ScheduleAsync(TaskId taskId, IDurableTaskRequest request, CancellationToken cancellationToken)
    {
        if (request.Context is not { } requestContext)
        {
            throw new InvalidOperationException($"No context for durable task request {request}");
        }

        // Check if the task is already running.
        if (TryGetScheduledTaskHandle(taskId, out var handle))
        {
            // If it is and it's completed, return the result immediately.
            var response = await handle.PollAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);
            if (response.IsCompleted)
            {
                return response;
            }

            // Subscribe the caller to the task if possible.
            if (_storage.TryGetTask(taskId, out var state) && TrySubscribeClient(taskId, state, GetCallerReferenceOrDefault(requestContext)))
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

            // Subscribe the caller to the task if possible.
            var subscribed = TrySubscribeClient(taskId, state, GetCallerReferenceOrDefault(requestContext));

            // If the task was already scheduled, return a response immediately.
            if (state.Result is { } response && response.IsCompleted)
            {
                return response;
            }

            // Persist the task state before invoking the task.
            // Note that if we intercept all outgoing calls to other durable tasks, then we do not need to do this here.
            // Instead, we can defer it until either the task completes or an outgoing call is made, since we can guarantee
            // no visible side-effects.
            // If the user does the 'wrong' thing and calls a non-durable task from their code, then that could expose an externality.
            await _storage.WriteAsync(cancellationToken);

            // Schedule the task with the runtime.
            var executionContext = CreateExecutionContext(taskId);
            handle = new TaskHandle(taskId, this) { IsRunning = true };
            _taskHandles.Add(taskId, handle);
            request.SetTarget(_shared.GrainContextAccessor.GrainContext);
            var invocationTask = Invoke(static request => request.CreateTask(), request, executionContext);
            _runningRequests.Add(taskId, invocationTask);

            return subscribed ? DurableTaskResponse.Subscribed : DurableTaskResponse.Pending;
        }
    }

    public async ValueTask<IScheduledTaskHandle> ScheduleChildAsync(TaskId taskId, DurableTask durableTask, CancellationToken cancellationToken)
    {
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
            var schedulingResponse = await schedulableTask.ScheduleAsync(taskId, cancellationToken);
            if (schedulingResponse.IsCompleted)
            {
                _storage.SetResponse(taskId, state, schedulingResponse);
                await _storage.WriteAsync(cancellationToken);

                return new CompletedTaskHandle(taskId, schedulingResponse);
            }
            
            // Schedule the task and store a handle to it in-memory.
            handle = schedulableTask.GetHandle(taskId);
            _taskHandles.Add(taskId, handle);
            await _storage.WriteAsync(cancellationToken);
            return handle;
        }

        // Otherwise, the task must be a local method invocation, so create an execution context for it and execute it.
        var executionContext = CreateExecutionContext(taskId);
        handle =  new TaskHandle(taskId, this) { IsRunning = true };
        _taskHandles.Add(taskId, handle);
        var invocationTask = Invoke(static task => task, durableTask, executionContext);
        _runningRequests.Add(taskId, invocationTask);
        return handle;
    }

    private async Task Invoke<TState>(Func<TState, DurableTask> createTask, TState state, GrainDurableExecutionContext context)
    {
        try
        {
            DurableTaskRuntimeHelper.SetCurrentContext(context);
            var response = await DurableTaskRuntimeHelper.RunAsync(createTask(state), context);
            await SetResponseAsync(context.TaskId, response, _deactivationCts.Token);
        }
        catch (Exception exception)
        {
            _shared.Logger.LogError(exception, "{Id} error invoking durable task '{DurableTask}'.", GrainId, createTask);
            await SetResponseAsync(context.TaskId, DurableTaskResponse.FromException(exception), _deactivationCts.Token);
        }
        finally
        {
            _runningRequests.Remove(context.TaskId);
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

        if (state.Result is null)
        {
            Debug.Assert(state.Result is null);

            // Store the result.
            // Note that this and the next call to notify callers may result in two writes in quick succession.
            // That is ok: we want to ensure that every client always sees the same result for a task, so it is important to persist the task before notifying the first client.
            _storage.SetResponse(taskId, state, response);
            await _storage.WriteAsync(cancellationToken);
        }

        if (_taskHandles.TryGetValue(taskId, out var handle))
        {
            if (handle is TaskHandle localHandle)
            {
                localHandle.TrySetResponse(response);
            }
        }

        await NotifyClientsAndCleanupTask(taskId, state, cancellationToken);
    }

    /// <summary>
    /// Notifies all subscribed clients that the task has completed and performs any necessary cleanup operations.
    /// </summary>
    /// <param name="taskId">The task which has completed.</param>
    /// <param name="state">The task execution context, containing the result.</param>
    /// <returns>A <see cref="Task"/> representing the work performed.</returns>
    private async Task NotifyClientsAndCleanupTask(TaskId taskId, IDurableTaskState state, CancellationToken cancellationToken)
    {
        Debug.Assert(state.Result is not null);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var clientTasks = new List<Task>();
                var clientCount = 0;

                if (state.Observers is { } clients)
                {
                    clientCount = clients.Count;
                    if (_shared.Logger.IsEnabled(LogLevel.Trace))
                    {
                        _shared.Logger.LogTrace("{Id} notifying {ClientsCount} clients for completion of task {TaskId}", GrainId, clientCount, taskId);
                    }

                    var response = state.Result;
                    foreach (var client in clients)
                    {
                        clientTasks.Add(client.OnResponseAsync(taskId, response, cancellationToken).AsTask());
                    }
                }

                await Task.WhenAll(clientTasks).WaitAsync(cancellationToken);

                _storage.ClearObservers(taskId, state);

                PruneCompletedTasks();

                // NOTE: this write is not required for correctness, so it could be removed & performed lazily.
                await _storage.WriteAsync(cancellationToken);

                if (_shared.Logger.IsEnabled(LogLevel.Trace))
                {
                    _shared.Logger.LogTrace("{Id} notified {ClientsCount} clients for completion of task {TaskId}", GrainId, clientCount, taskId);
                }

                // Success, no more work to be done right now.
                break;
            }
            catch (Exception exception)
            {
                _shared.Logger.LogWarning(exception, "{Id} exception while notifying clients of completion for durable task {TaskId}", GrainId, taskId);
            }

            // TODO: Make this configurable and probably use exponential back-off, potentially with some coordination with other tasks.
            await Task.Delay(TimeSpan.FromSeconds(10), _shared.TimeProvider, cancellationToken);
        }
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
            if (state.Result is null)
            {
                // The task is incomplete.
                continue;
            }

            if (state.Observers is { Count: > 0 })
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

        var client = options.Observer;
        if (client is not null && _storage.TryGetTask(taskId, out var state) && TrySubscribeClient(taskId, state, client))
        {
            await _storage.WriteAsync(cancellationToken);
            return DurableTaskResponse.Subscribed;
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
                Waiters = taskState.Observers?.Select(static client => client.ToString()!).ToList() ?? [],
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
        if (taskId.IsDefault)
        {
            throw new ArgumentException("Invalid TaskId.", nameof(taskId));
        }

        if (!_storage.TryGetTask(taskId, out var taskState))
        {
            // The task may have been pruned or may never have existed.
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
            tasks.Add(DurableTaskRuntimeHelper.CancelAsync(context, cancellationToken));
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

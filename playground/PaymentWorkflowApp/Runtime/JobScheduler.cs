using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Distributed.DurableTasks;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
namespace PaymentWorkflowApp.Runtime;

public class JobDescription
{
    public TaskId JobId { get; set; }
    public string? Status { get; set; }
    public string? Result { get; set; }
    public Exception? Exception { get; set; }

    public string? Type { get; set; }

    public string[]? Arguments { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public override string? ToString() => $"[Id: {JobId}, Status: {Status}, Type: {Type}, Arguments: {string.Join(", ", Arguments ?? Array.Empty<string>())}, CreatedAt: {CreatedAt}, CompletedAt: {CompletedAt}, Result: {Result}, Exception: {Exception?.GetType()}]";
}

public class JobScheduler(IJobStorage storage, ILogger<JobScheduler> logger)
{
    private readonly Dictionary<string, object> _handlers = [];
    private readonly Dictionary<TaskId, JobDurableExecutionContext> _tasks = [];
    private readonly Dictionary<TaskId, Task> _runningTasks = [];
    private readonly IJobStorage _storage = storage;
    private readonly ILogger<JobScheduler> _logger = logger;
    private readonly SemaphoreSlim _asyncLock = new(1);

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await RecoverAsync(cancellationToken);
    }

    private async ValueTask RecoverAsync(CancellationToken cancellationToken)
    {
        await _storage.ReadAsync(cancellationToken);
        foreach (var (taskId, taskState) in _storage.Tasks)
        {
            if (taskState.Result is null)
            {
                var job = new JobTask(taskState.Type!, taskState.Arguments, this);
                var executionContext = RehydrateTaskFromStorage(taskId, taskState);
                InvokeRequestMethod(job, taskId, executionContext, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Creates a new execution context, registering it in the local collection of execution contexts.
    /// </summary>
    /// <param name="taskId">The task id.</param>
    /// <param name="state">The task state.</param>
    /// <returns>The new execution context.</returns>
    private JobDurableExecutionContext CreateExecutionContext(TaskId taskId, JobTaskState state) => _tasks[taskId] = new JobDurableExecutionContext(taskId, this, state);

    /// <summary>
    /// Gets the execution context corresponding to the provided task, if it exists, and returns it.
    /// </summary>
    /// <param name="taskId">The task to get an execution context from.</param>
    /// <param name="executionContext">The execution context.</param>
    /// <returns><see langword="true"/> if the execution context was found, <see langword="false"/> otherwise.</returns>
    private bool TryGetExecutionContext(TaskId taskId, [NotNullWhen(true)] out JobDurableExecutionContext? executionContext)
    {
        // Is an active method already waiting for this?
        if (_tasks.TryGetValue(taskId, out executionContext))
        {
            return true;
        }

        if (_storage.TryGetTask(taskId, out var state))
        {
            executionContext = RehydrateTaskFromStorage(taskId, state);
            return true;
        }

        return false;
    }

    private JobDurableExecutionContext RehydrateTaskFromStorage(TaskId taskId, JobTaskState state)
    {
        // Rehydrate the execution context from its persisted state.
        var executionContext = new JobDurableExecutionContext(taskId, this, state);

        // If the task has completed, set the result now.
        if (state.Result is { } response)
        {
            //DurableTaskRuntimeHelper.SetResult(executionContext, response);
            throw new NotImplementedException();
        }

        // Move the task into the list of active tasks.
        _tasks[taskId] = executionContext;
        return executionContext;
    }

    private async Task<JobDurableExecutionContext> CreateExecutionContextAsync(TaskId taskId, string? type, string[]? arguments, CancellationToken cancellationToken)
    {
        var newTaskState = new JobTaskState
        {
            CreatedAt = DateTime.UtcNow,
            Type = type,
            Arguments = arguments,
        };

        _storage.AddOrUpdateTask(taskId, newTaskState);
        await _storage.WriteAsync(cancellationToken);

        return CreateExecutionContext(taskId, newTaskState);
    }

    public void AddHandler(string jobType, Func<string[], DurableTask<string>> handler)
    {
        _handlers[jobType] = handler;
    }
    public void AddHandler(string jobType, Func<string[], string> handler) => _handlers[jobType] = handler;

    public DurableTask<string> CreateJob(string jobType, params string[]? args) => new JobTask(jobType, args, this);

    public async IAsyncEnumerable<JobDescription> GetJobsAsync(bool includeCompleted = true)
    {
        await Task.Yield();
        foreach (var (taskId, job) in _tasks)
        {
            var (result, error) = job.State.Result switch
            {
                { Exception: Exception exception } => (null, exception),
                { Result: object res } => (res, null),
                _ => (default(object), default(Exception)),
            };

            if ((result is not null || error is not null) && !includeCompleted)
            {
                continue;
            }

            var isRunning = _runningTasks.ContainsKey(taskId);
            var description = new JobDescription
            {
                JobId = taskId,
                Arguments = job.State.Arguments,
                Type = job.State.Type,
                Status = (result, error) switch { (null, null) when isRunning => "Running", (null, null) => "Pending", (not null, null) => "Completed", (null, not null) => "Faulted", _ => "Internal Error" },
                Exception = error,
                Result = result?.ToString(),
                CreatedAt = job.State.CreatedAt,
                CompletedAt = job.State.CompletedAt,
            };
            yield return description;
        }
    }

    internal async ValueTask<DurableTaskResponse> ScheduleAsync(JobTask job, TaskId taskId, CancellationToken cancellationToken)
    {
        try
        {
            await _asyncLock.WaitAsync();

            if (TryGetExecutionContext(taskId, out var executionContext))
            {
                if (!string.Equals(executionContext.State.Type, job.Type, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Attempt to schedule multiple jobs with the same task id, {taskId}, but different job types. Scheduled job type: {executionContext.State.Type}. Requested job type: {job.Type}");
                }

                // and similarly for the job arguments...
                //return DurableTaskRuntimeHelper.Poll(executionContext);
                throw new NotImplementedException();
            }

            executionContext = await CreateExecutionContextAsync(taskId, job.Type, job.Arguments, cancellationToken);

            // Start invoking the newly defined task.
            InvokeRequestMethod(job, taskId, executionContext, cancellationToken);

            //return DurableTaskRuntimeHelper.Poll(executionContext);
            throw new NotImplementedException();
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    public async ValueTask<IScheduledTaskHandle> InvokeAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
    {
        var ctx = await EvaluateStepAsync(taskId, taskDefinition, cancellationToken);

        //return await DurableTaskRuntimeHelper.WaitAsync(ctx, cancellationToken);
        throw new NotImplementedException();
    }

    private async ValueTask<DurableExecutionContext> EvaluateStepAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken)
    {
        try
        {
            await _asyncLock.WaitAsync(cancellationToken);

            if (!TryGetExecutionContext(taskId, out var executionContext))
            {
                executionContext = await CreateExecutionContextAsync(taskId, type: null, arguments: null, cancellationToken);
            }

            try
            {
                var response = await DurableTaskRuntimeHelper.RunAsync(taskDefinition, executionContext);
                await CompleteRequestWithResponse(taskId, response, executionContext, cancellationToken);
            }
            catch (Exception exception)
            {
                await CompleteRequestWithResponse(taskId, DurableTaskResponse.FromException(exception), executionContext, cancellationToken);
            }

            return executionContext;
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    private void InvokeRequestMethod(JobTask job, TaskId taskId, JobDurableExecutionContext executionContext, CancellationToken cancellationToken)
    {
        _runningTasks.Add(taskId, InvokeRequestMethodCore(taskId, job, executionContext, cancellationToken));
    }

    private async Task InvokeRequestMethodCore(TaskId taskId, JobTask job, JobDurableExecutionContext executionContext, CancellationToken cancellationToken)
    {
        await Task.Yield();

        try
        {
            var response = await DurableTaskRuntimeHelper.RunAsync(job, executionContext);

            await CompleteRequestWithResponse(taskId, response, executionContext, cancellationToken);
        }
        catch (Exception exception)
        {
            var arguments = job.Arguments;
            var argString = arguments switch { { Length: 1 } arg => arg[0], { Length: > 1 } => string.Join(", ", arguments), _ => "" };
            _logger.LogError(exception, "Error invoking durable task request {Type}({Arguments})", job.Type, argString);
            await CompleteRequestWithResponse(taskId, DurableTaskResponse.FromException(exception), executionContext, cancellationToken);
        }
    }

    private async Task CompleteRequestWithResponse(TaskId taskId, DurableTaskResponse response, JobDurableExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("Task {TaskId} completed with result {Result}", taskId, response);
        }

        // Only update the result if an existing result has not been set. If this were to overwrite an already-persisted result,
        // that could cause the result to appear to change after it has already been observed.
        // This condition guards against the case where a scheduling call fails after the response has already been received via an OnResponse callback,
        // which could occur due to a recovery retry or concurrency (multiple clients scheduling the same workflow).
        var state = executionContext.State;
        if (state.Result is null)
        {
            Debug.Assert(state.Result is null);

            // Store the result.
            // Note that this and the next call to notify callers may result in two writes in quick succession.
            // That is ok: we want to ensure that every client always sees the same result for a task, so it is important to persist the task before notifying the first client.
            state.Result = response;
            state.CompletedAt = DateTime.UtcNow;
            _storage.AddOrUpdateTask(taskId, state);
            await _storage.WriteAsync(cancellationToken);

            // TODO: visibility of results must only happen once all child tasks have completed.
            //DurableTaskRuntimeHelper.SetResult(executionContext, response);
            throw new NotImplementedException();
        }
    }

    public async Task PruneCompletedTasksAsync(TimeSpan cleanupAge, CancellationToken cancellationToken = default)
    {
        try
        {
            await _asyncLock.WaitAsync();
            await _storage.ReadAsync(cancellationToken);
            if (PruneInternal(DateTime.UtcNow, cleanupAge))
            {
                _logger.LogInformation("Pruned expired, completed tasks");
                await _storage.WriteAsync(cancellationToken);
            }
            else
            {
                _logger.LogInformation("No expired, completed tasks to prune");
            }
        }
        finally
        {
            _asyncLock.Release();
        }
    }

    private bool PruneInternal(DateTime now, TimeSpan cleanupAge)
    {
        // Prune all tasks which:
        // * Have a response
        // * Have no parents waiting on them within this context
        // * Have been completed for more than a configured period of time
        var allTasks = _storage.Tasks.ToDictionary(static task => task.Id, static task => task.State);
        HashSet<TaskId>? completedTaskIds = default;
        Dictionary<TaskId, HashSet<TaskId>>? waitingOnParent = default;
        foreach (var (taskId, state) in allTasks)
        {
            if (state.Result is null)
            {
                // The task is incomplete.
                continue;
            }

            if (state.CompletedAt is not { } completedAt || now.Subtract(completedAt) < cleanupAge)
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
                        if (_logger.IsEnabled(LogLevel.Trace))
                        {
                            _logger.LogTrace("Pruning completed child task {TaskId}", childTaskId);
                        }

                        _storage.RemoveTask(childTaskId);
                    }
                }

                // Prune the task.
                if (_logger.IsEnabled(LogLevel.Trace))
                {
                    _logger.LogTrace("Pruning completed task {TaskId}", taskId);
                }
                _storage.RemoveTask(taskId);
            }
        }

        return completedTaskIds is not null;
    }

    internal ValueTask SignalCancellationAsync(TaskId id, JobTaskState state)
    {
        throw new NotImplementedException();
        //_storage.
    }

    internal IScheduledTaskHandle GetScheduledTaskHandle(TaskId taskId)
    {
        throw new NotImplementedException();
    }

    internal class JobTask(string type, string[]? args, JobScheduler jobScheduler) : DurableTask<string>, ISchedulableTask
    {
        private readonly JobScheduler _jobScheduler = jobScheduler;
        public string[]? Arguments { get; } = args;
        public string Type { get; } = type;

        public IScheduledTaskHandle GetHandle(TaskId taskId)
        {
            throw new NotImplementedException();
        }

        public ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken)
        {
            return _jobScheduler.ScheduleAsync(this, taskId, cancellationToken);
        }

        protected override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext executionContext)
        {
            var handler = _jobScheduler._handlers[Type];
            DurableTaskResponse response;
            if (handler is Func<string[]?, string> funcJob)
            {
                DurableTaskRuntimeHelper.SetCurrentContext(executionContext);
                response = DurableTaskResponse.FromResult(funcJob(Arguments));

            }
            else if (handler is Func<string[]?, DurableTask<string>> durableTaskJob)
            {
                // This might be a bit confusing: we are forwarding the invocation on to the async method.
                response = await DurableTaskRuntimeHelper.RunAsync(durableTaskJob(Arguments), executionContext);
            }
            else
            {
                // Add other types...
                throw new NotSupportedException($"Job handlers of type {handler.GetType()} are not supported.");
            }

            return response;
        }
    }
}

public sealed class SingleThreadedJobScheduler(ILogger<JobScheduler> logger) : SynchronizationContext, IThreadPoolWorkItem
{
    private static int NextSchedulerId = 0;
    private enum RunState
    {
        Waiting = 0,
        Runnable = 1,
        Running = 2
    }

    private readonly Queue<(object Callback, object? State)> _workItems = new();
    private readonly object _lock = new();
    private readonly ILogger<JobScheduler> _logger = logger;
    private readonly int _id = Interlocked.Increment(ref NextSchedulerId);
    private RunState _state;

    public override string ToString() => $"{nameof(SingleThreadedJobScheduler)}-{_id}";

    public override void Post(SendOrPostCallback callback, object? state) => Schedule(callback, state);
    public override void Send(SendOrPostCallback callback, object? state) => Schedule(callback, state);

    public void Schedule(object callback, object? state)
    {
        lock (_lock)
        {
            _workItems.Enqueue((callback, state));
            if (_state is not RunState.Waiting)
            {
                return;
            }

            _state = RunState.Runnable;
        }

        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
    }

    public void ScheduleOrRunInline(object callback, object? state)
    {
        lock (_lock)
        {
            if (_state is not RunState.Running)
            {
                // Note that this holds the lock for the duration of the task execution, which is hopefully short.
                RunTaskInline(callback, state);
            }
            else
            {
                _workItems.Enqueue((callback, state));
                _state = RunState.Runnable;
            }
        }
    }

    private void RunTaskInline(object callback, object? state)
    {
        if (callback is SendOrPostCallback sendOrPostCallback)
        {
            var existing = Current;
            SetSynchronizationContext(this);
            try
            {
                sendOrPostCallback(state);
            }
            finally
            {
                SetSynchronizationContext(existing);
            }
        }
        else
        {
            throw new InvalidOperationException($"Unknown task type for callback {callback} ({callback.GetType()})");
        }
    }

    void IThreadPoolWorkItem.Execute()
    {
        try
        {
            while (true)
            {
                lock (_lock)
                {
                    _state = RunState.Running;
                }

                // Get the first work item from the queue
                (object Callback, object? State) item;
                lock (_lock)
                {
                    if (!_workItems.TryDequeue(out item))
                    {
                        break;
                    }
                }

                RunTaskInline(item.Callback, item.State);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error invoking scheduled callback");
        }
        finally
        {
            lock (_lock)
            {
                if (_workItems.Count > 0)
                {
                    _state = RunState.Runnable;
                    ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: true);
                }
                else
                {
                    _state = RunState.Waiting;
                }
            }
        }
    }
}

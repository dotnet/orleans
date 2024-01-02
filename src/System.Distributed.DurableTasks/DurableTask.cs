using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace System.Distributed.DurableTasks;

[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract class DurableTask
{
    public static DurableTask<TResult> FromResult<TResult>(TResult value) => new CompletedDurableTask<TResult>(value);

    public static DurableTask Run(Action<CancellationToken> func) => new DelegateDurableTask(func);
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, TResult> func) => new DelegateDurableTask<TResult>(func);
    public static DurableTask Run(Func<CancellationToken, Task> func) => new AsyncTaskDelegateDurableTask(func);
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, Task<TResult>> func) => new AsyncTaskDelegateDurableTask<TResult>(func);

    public static DurableTask Run<TState>(Action<TState, CancellationToken> func, TState state) => new DelegateDurableTaskWithState<TState>(func, state);
    public static DurableTask<TResult> Run<TState, TResult>(Func<TState, CancellationToken, TResult> func, TState state) => new DelegateDurableTaskWithState<TState, TResult>(func, state);
    public static DurableTask Run<TState>(Func<TState, CancellationToken, Task> func, TState state) => new AsyncTaskDelegateDurableTaskWithState<TState>(func, state);
    public static DurableTask<TResult> Run<TState, TResult>(Func<TState, CancellationToken, Task<TResult>> func, TState state) => new AsyncTaskDelegateDurableTaskWithState<TState, TResult>(func, state);

    public static DurableTask<List<ScheduledTask>> WhenAll(List<DurableTask> tasks)
        => Run(async (tasks, cancellationToken) =>
    {
        if (DurableExecutionContext.CurrentContext is null)
        {
            throw new InvalidOperationException($"{nameof(DurableTask)}.{nameof(WhenAll)} can only be used within a durable task context.");
        }

        var result = new List<ScheduledTask>();
        for (var i = 0; i < tasks.Count; i++)
        {
            await tasks[i].ScheduleAsync($"{i}", cancellationToken);
        }

        foreach (var task in result)
        {
            await task.GetResponseAsync(cancellationToken);
        }

        return result;
    }, tasks);

    public static DurableTask<List<ScheduledTask<TResult>>> WhenAll<TResult>(List<DurableTask<TResult>> tasks)
        => Run(async (tasks, cancellationToken) =>
    {
        if (DurableExecutionContext.CurrentContext is null)
        {
            throw new InvalidOperationException($"{nameof(DurableTask)}.{nameof(WhenAll)} can only be used within a durable task context.");
        }

        var result = new List<ScheduledTask<TResult>>();
        for (var i = 0; i < tasks.Count; i++)
        {
            await tasks[i].ScheduleAsync($"{i}", cancellationToken: cancellationToken);
        }

        foreach (var task in result)
        {
            await task.GetResponseAsync(cancellationToken);
        }

        return result;
    }, tasks);

    public static DurableTask<ScheduledTask> WhenAny(List<DurableTask> tasks)
    => Run(async (tasks, cancellationToken) =>
    {
        if (DurableExecutionContext.CurrentContext is null)
        {
            throw new InvalidOperationException($"{nameof(DurableTask)}.{nameof(WhenAny)} can only be used within a durable task context.");
        }

        var result = new List<ScheduledTask>();
        for (var i = 0; i < tasks.Count; i++)
        {
            await tasks[i].ScheduleAsync($"{i}", cancellationToken);
        }

        var completions = new List<Task>(result.Count);
        foreach (var task in result)
        {
            completions.Add(task.GetResponseAsync(cancellationToken));
        }

        var completed = await Task.WhenAny(completions);

        return result[completions.IndexOf(completed)];
    }, tasks);

    public static DurableTask<ScheduledTask<TResult>> WhenAny<TResult>(List<DurableTask<TResult>> tasks)
    => Run(async (tasks, cancellationToken) =>
    {
        if (DurableExecutionContext.CurrentContext is null)
        {
            throw new InvalidOperationException($"{nameof(DurableTask)}.{nameof(WhenAny)} can only be used within a durable task context.");
        }

        var result = new List<ScheduledTask<TResult>>();
        for (var i = 0; i < tasks.Count; i++)
        {
            await tasks[i].ScheduleAsync($"{i}", cancellationToken);
        }

        var completions = new List<Task>(result.Count);
        foreach (var task in result)
        {
            completions.Add(task.GetResponseAsync(cancellationToken));
        }

        var completed = await Task.WhenAny(completions);

        return result[completions.IndexOf(completed)];
    }, tasks);

    /// <summary>
    /// Invokes the task with the provided context.
    /// </summary>
    /// <param name="context">The task context.</param>
    /// <returns>The response.</returns>
    protected internal abstract ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context);
}

[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask
{
}

internal struct ConfiguredDurableTaskCore<TDurableTask> where TDurableTask : DurableTask
{
    internal readonly TDurableTask Task;
    internal readonly DurableExecutionContext? ParentContext = DurableExecutionContext.CurrentContext;
    private TaskId _taskId;

    public ConfiguredDurableTaskCore(TDurableTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Task = task;
    }

    public readonly TaskId TaskId => _taskId;

    public void SetTaskId(string? taskId)
    {
        if (!TrySetTaskId(taskId))
        {
            throw new InvalidOperationException($"This task's {nameof(DurableTasks.TaskId)} has already been specified.");
        }
    }

    internal async Task<DurableTaskResponse> RunAsync(CancellationToken cancellationToken)
    {
        // Ensure the task id is set.
        _ = TrySetTaskId(null);

        IScheduledTaskHandle handle;
        if (ParentContext is { } parentContext)
        {
            handle = await parentContext.ScheduleChildTaskAsync(_taskId, Task, cancellationToken);
        }
        else if (Task is ISchedulableTask schedulableTask)
        {
            var response = await schedulableTask.ScheduleAsync(_taskId, cancellationToken);
            if (response.IsCompleted)
            {
                return response;
            }

            handle = schedulableTask.GetHandle(_taskId);
        }
        else
        {
            throw ConfiguredDurableTask.GetNonSchedulableTaskException();
        }

        return await handle.WaitAsync(cancellationToken);
    }

    internal bool TrySetTaskId(string? name)
    {
        if (!_taskId.IsDefault)
        {
            return false;
        }

        if (ParentContext is { } parentContext)
        {
            // Create an identifier relative to the parent context's identifier.
            _taskId = parentContext.CreateChildTaskId(name);
        }
        else if (string.IsNullOrWhiteSpace(name))
        {
            _taskId = TaskId.CreateRandom();
        }
        else
        {
            _taskId = TaskId.Create(name);
        }

        return true;
    }
    
    // Schedules a durable task without waiting for the task to complete
    public async Task<ScheduledTask> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        // Ensure the task id is set.
        _ = TrySetTaskId(null);

        IScheduledTaskHandle handle;
        if (ParentContext is { } parentContext)
        {
            handle = await parentContext.ScheduleChildTaskAsync(_taskId, Task, cancellationToken);
        }
        else if (Task is ISchedulableTask schedulableTask)
        {
            var response = await schedulableTask.ScheduleAsync(TaskId, cancellationToken);
            if (response.IsCompleted)
            {
                return new CompletedScheduledDurableTask(TaskId, response);
            }

            handle = schedulableTask.GetHandle(TaskId);
        }
        else
        {
            throw ConfiguredDurableTask.GetNonSchedulableTaskException();
        }

        return new ScheduledDurableTask(handle);
    }

    // Cancels a durable task without waiting for the task to complete
    public async Task<bool> CancelAsync(CancellationToken cancellationToken)
    {
        // Ensure the task id is set.
        _ = TrySetTaskId(null);

        var handle = GetHandleOrThrow();
        await handle.CancelAsync(cancellationToken);
        return true;
    }

    // Polls a task, returning the status of the task.
    public async Task<DurableTaskStatus> PollAsync(PollingOptions pollingOptions, CancellationToken cancellationToken)
    {
        // Ensure the task id is set.
        _ = TrySetTaskId(null);

        var handle = GetHandleOrThrow();
        var response = await handle.PollAsync(pollingOptions, cancellationToken);
        return response.Status;
    }

    private readonly IScheduledTaskHandle GetHandleOrThrow()
    {
        IScheduledTaskHandle handle;
        if (ParentContext is { } parentContext)
        {
            handle = parentContext.GetChildTaskHandle(TaskId);
        }
        else if (Task is ISchedulableTask schedulableTask)
        {
            handle = schedulableTask.GetHandle(TaskId);
        }
        else
        {
            throw ConfiguredDurableTask.GetNonSchedulableTaskException();
        }

        return handle;
    }
}

public struct ConfiguredDurableTask(DurableTask task)
{
    private ConfiguredDurableTaskCore<DurableTask> _core = new(task);

    public DurableTaskAwaiter GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    internal readonly DurableTask Task => _core.Task;
    internal readonly TaskId TaskId => _core.TaskId;

    internal ConfiguredDurableTask WithId(string taskId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(taskId);
        _core.TrySetTaskId(taskId);
        return this;
    }

    // Schedules a durable task without waiting for the task to complete
    public Task<ScheduledTask> ScheduleAsync(CancellationToken cancellationToken = default) => _core.ScheduleAsync(cancellationToken);

    // Cancels a durable task without waiting for the task to complete
    public Task<bool> CancelAsync(CancellationToken cancellationToken) => _core.CancelAsync(cancellationToken);

    // Polls a task, returning the status of the task.
    public Task<DurableTaskStatus> PollAsync(PollingOptions pollingOptions, CancellationToken cancellationToken) => _core.PollAsync(pollingOptions, cancellationToken);

    internal static InvalidOperationException GetNonSchedulableTaskException() => new("The provided task does not support scheduling. This may be because it is a local method or another non-serializable task type.");
}

public struct ConfiguredDurableTask<TResult>(DurableTask<TResult> task)
{
    private ConfiguredDurableTaskCore<DurableTask<TResult>> _core = new(task);

    internal readonly DurableTask<TResult> Task => _core.Task;
    internal readonly TaskId TaskId => _core.TaskId;

    public DurableTaskAwaiter<TResult> GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    public ConfiguredDurableTask<TResult> WithId(string taskId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(taskId);
        _core.TrySetTaskId(taskId);
        return this;
    }

    // Schedules a durable task without waiting for the task to complete
    public async Task<ScheduledTask<TResult>> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        // Ensure the task id is set.
        _core.TrySetTaskId(null);

        IScheduledTaskHandle handle;
        if (_core.ParentContext is { } parentContext)
        {
            handle = await parentContext.ScheduleChildTaskAsync(TaskId, Task, cancellationToken);
        }
        else if (Task is ISchedulableTask schedulableTask)
        {
            var response = await schedulableTask.ScheduleAsync(TaskId, cancellationToken);
            if (response.IsCompleted)
            {
                return new CompletedScheduledDurableTask<TResult>(TaskId, response);
            }

            handle = schedulableTask.GetHandle(TaskId);
        }
        else
        {
            throw ConfiguredDurableTask.GetNonSchedulableTaskException();
        }

        return new ScheduledDurableTask<TResult>(handle);
    }

    // Cancels a durable task without waiting for the task to complete
    public Task<bool> CancelAsync(CancellationToken cancellationToken) => _core.CancelAsync(cancellationToken);

    // Polls a task, returning the status of the task.
    public Task<DurableTaskStatus> PollAsync(PollingOptions pollingOptions, CancellationToken cancellationToken) => _core.PollAsync(pollingOptions, cancellationToken);
}

/// <summary>
/// Represents a completed <see cref="DurableTask{TResult}"/> instance.
/// </summary>
internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context) => new(DurableTaskResponse.FromResult(value));
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTask<TResult>(Func<CancellationToken, Task<TResult>> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        try
        {
            DurableExecutionContext.SetCurrentContext(context);
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            return DurableTaskResponse.FromResult(await func(cts.Token));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTask(Func<CancellationToken, Task> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        try
        {
            DurableExecutionContext.SetCurrentContext(context);
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            await func(cts.Token);
            return DurableTaskResponse.Completed;
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask<TResult>(Func<CancellationToken, TResult> func) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        DurableExecutionContext.SetCurrentContext(context, out var previousContext);
        try
        {
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            return new(DurableTaskResponse.FromResult(func(cts.Token)));
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTask(Action<CancellationToken> func) : DurableTask
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        DurableExecutionContext.SetCurrentContext(context, out var previousContext);
        try
        {
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            func(cts.Token);
            return new(DurableTaskResponse.Completed);
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, Task<TResult>> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        try
        {
            DurableExecutionContext.SetCurrentContext(context);
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            return DurableTaskResponse.FromResult(await func(state, cts.Token));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncTaskDelegateDurableTaskWithState<TState>(Func<TState, CancellationToken, Task> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        try
        {
            DurableExecutionContext.SetCurrentContext(context);
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            await func(state, cts.Token);
            return DurableTaskResponse.Completed;
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class AsyncDelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, ValueTask<TResult>> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        try
        {
            DurableExecutionContext.SetCurrentContext(context);
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            return DurableTaskResponse.FromResult(await func(state, cts.Token));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask{TResult}"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTaskWithState<TState, TResult>(Func<TState, CancellationToken, TResult> func, TState state) : DurableTask<TResult>
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        DurableExecutionContext.SetCurrentContext(context, out var previousContext);
        try
        {
            DurableExecutionContext.SetCurrentContext(context);
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            return new(DurableTaskResponse.FromResult(func(state, cts.Token)));
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }
    }
}

/// <summary>
/// Represents a <see cref="DurableTask"/> instance which invokes a delegate.
/// </summary>
internal sealed class DelegateDurableTaskWithState<TState>(Action<TState, CancellationToken> func, TState state) : DurableTask
{
    /// <inheritdoc/>
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        DurableExecutionContext.SetCurrentContext(context, out var previousContext);
        try
        {
            DurableExecutionContext.SetCurrentContext(context);
            using var cts = new CancellationTokenSource();
            using var registration = context.RegisterCancellationCallback(static (cts, ct) => cts!.CancelAsync(), cts);
            func(state, cts.Token);
            return new(DurableTaskResponse.Completed);
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
        finally
        {
            DurableExecutionContext.SetCurrentContext(previousContext);
        }
    }
}

public enum DurableTaskStatus
{
    None,
    Pending,
    CompletedSuccessfully,
    Canceled,
    Failed,
}

public enum DurableTaskResponseKind
{
    None,
    Pending,
    Subscribed,
    CompletedSuccessfully,
    Failed,
}

internal static class DurableTaskResponseExtensions
{
    public static bool IsCompleted(this DurableTaskStatus value) => value is DurableTaskStatus.CompletedSuccessfully or DurableTaskStatus.Canceled or DurableTaskStatus.Failed;
    public static bool IsCompleted(this DurableTaskResponseKind value) => value is DurableTaskResponseKind.CompletedSuccessfully or DurableTaskResponseKind.Failed;
}

/// <summary>
/// Represents the result of a method invocation.
/// </summary>
public abstract class DurableTaskResponse
{
    // Internal constructor to prevent external inheritance.
    internal DurableTaskResponse()
    {
    }

    /// <summary>
    /// Creates a new response representing an exception.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>A new response.</returns>
    public static DurableTaskResponse FromException(Exception exception) => new ExceptionDurableTaskResponse(exception);

    /// <summary>
    /// Creates a new response object which has been fulfilled with the provided value.
    /// </summary>
    /// <typeparam name="TResult">The underlying result type.</typeparam>
    /// <param name="value">The value.</param>
    /// <returns>A new response.</returns>
    public static DurableTaskResponse<TResult> FromResult<TResult>(TResult value) => new(value);

    /// <summary>
    /// Gets a completed response with no value.
    /// </summary>
    public static DurableTaskResponse Completed => SuccessDurableTaskResponse.Instance;

    /// <summary>
    /// Gets a pending response.
    /// </summary>
    public static DurableTaskResponse Pending => PendingDurableTaskResponse.Instance;

    /// <summary>
    /// Gets a subscribed response.
    /// </summary>
    public static DurableTaskResponse Subscribed => SubscribedDurableTaskResponse.Instance;

    /// <summary>
    /// Gets a value indicating whether the response represents a completed task.
    /// </summary>
    /// <remarks>
    /// A task is considered completed if it has either succeeded, failed, or was canceled.
    /// </remarks>
    public bool IsCompleted => ResponseKind.IsCompleted();

    /// <summary>
    /// Gets the response kind.
    /// </summary>
    public abstract DurableTaskResponseKind ResponseKind { get; }

    /// <summary>
    /// Gets the response status.
    /// </summary>
    public DurableTaskStatus Status => ResponseKind switch
    {
        DurableTaskResponseKind.CompletedSuccessfully => DurableTaskStatus.CompletedSuccessfully,
        DurableTaskResponseKind.Subscribed => DurableTaskStatus.Pending,
        DurableTaskResponseKind.Pending => DurableTaskStatus.Pending,
        DurableTaskResponseKind.Failed when Exception is OperationCanceledException => DurableTaskStatus.Canceled,
        DurableTaskResponseKind.Failed => DurableTaskStatus.Failed,
        _ => throw new NotSupportedException($"Unknown response kind '{ResponseKind}'."),
    };

    /// <summary>
    /// Gets the result value.
    /// </summary>
    /// <remarks>
    /// If the response represents an exception, this property will throw the exception.
    /// If the response represents an incomplete task, this property will throw an exception.
    /// </remarks>
    public abstract object? Result { get; }

    /// <summary>
    /// Gets the static type of the result value, or <see langword="null"/> if this response does not have a result value.
    /// </summary>
    public virtual Type? ResultType => null;

    /// <summary>
    /// Gets the exception or <see langword="null" /> if the response does not represent an exception.
    /// </summary>
    public abstract Exception? Exception { get; }

    /// <summary>
    /// Gets the result value with the specified type.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <returns>The result value.</returns>
    public abstract T GetResult<T>();
}

/// <summary>
/// Represents a successfully completed task.
/// </summary>
public sealed class SuccessDurableTaskResponse : DurableTaskResponse
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static SuccessDurableTaskResponse Instance { get; } = new SuccessDurableTaskResponse();

    /// <inheritdoc/>
    public override object? Result => null;

    /// <inheritdoc/>
    public override Exception? Exception => null;

    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.CompletedSuccessfully;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override string ToString() => "[Success]";
}

/// <summary>
/// A <see cref="DurableTaskResponse"/> which represents an exception, a broken promise.
/// </summary>
public sealed class ExceptionDurableTaskResponse : DurableTaskResponse
{
    public ExceptionDurableTaskResponse(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Exception = exception;
    }

    /// <inheritdoc/>
    public override object? Result
    {
        get
        {
            ExceptionDispatchInfo.Capture(Exception!).Throw();
            return null;
        }
    }

    /// <inheritdoc/>
    public override Exception Exception { get; }

    /// <inheritdoc/>
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.Failed;

    /// <inheritdoc/>
    public override T GetResult<T>()
    {
        ExceptionDispatchInfo.Capture(Exception!).Throw();
        return default;
    }

    /// <inheritdoc/>
    public override string ToString() => $"[Error: {Exception?.ToString()}]";
}

/// <summary>
/// A <see cref="DurableTaskResponse"/> which represents a typed value.
/// </summary>
/// <typeparam name="TResult">The underlying result type.</typeparam>
public sealed class DurableTaskResponse<TResult>(TResult result) : DurableTaskResponse
{
    public TResult TypedResult { get => result; }

    public override Exception? Exception => null;

    public override object? Result => result;

    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.CompletedSuccessfully;

    public override Type ResultType => typeof(TResult);

    public override T GetResult<T>()
    {
        if (typeof(TResult).IsValueType && typeof(T).IsValueType && typeof(T) == typeof(TResult))
            return Unsafe.As<TResult, T>(ref result!);

        return (T)(object)result!;
    }

    public override string ToString() => $"[Success: '{result?.ToString()}']";
}

/// <summary>
/// Represents a pending result for a <see cref="DurableTask"/> or <see cref="DurableTask{TResult}"/> invocation.
/// </summary>
public sealed class PendingDurableTaskResponse : DurableTaskResponse
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static PendingDurableTaskResponse Instance { get; } = new();

    /// <inheritdoc/>
    public override object? Result => throw new InvalidOperationException("The task has not completed yet.");

    /// <inheritdoc/>
    public override Exception? Exception => null;

    /// <inheritdoc/>
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.Pending;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override string ToString() => "[Pending]";
}

/// <summary>
/// Represents an intermediary response indicating that the caller is subscribed to the task and the task has not completed yet.
/// </summary>
public sealed class SubscribedDurableTaskResponse : DurableTaskResponse
{
    /// <summary>
    /// Gets the singleton instance of this class.
    /// </summary>
    public static SubscribedDurableTaskResponse Instance { get; } = new();

    /// <inheritdoc/>
    public override object? Result => throw new InvalidOperationException("The task has not completed yet.");

    /// <inheritdoc/>
    public override Exception? Exception => null;

    /// <inheritdoc/>
    public override DurableTaskResponseKind ResponseKind => DurableTaskResponseKind.Subscribed;

    /// <inheritdoc/>
    public override T GetResult<T>() => default!;

    /// <inheritdoc/>
    public override string ToString() => "[Subscribed]";
}

internal static class ResponseExtensions
{
    public static void ThrowIfExceptionResponse(this DurableTaskResponse response)
    {
        if (response.Exception is { } exception)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }
}

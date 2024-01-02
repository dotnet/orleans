using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Represents an operation which is scheduled and will complete at an indefinite point in the future.
/// </summary>
public abstract class ScheduledTask
{
    internal ScheduledTask() { }

    /// <summary>
    /// Gets the task identifier.
    /// </summary>
    public abstract TaskId Id { get; }

    /// <summary>Gets an awaiter used to await this <see cref="ScheduledTask"/>.</summary>
    /// <returns>An awaiter instance.</returns>
    public ScheduledTaskAwaiter GetAwaiter() => new(this, cancellationToken: default);

    /// <summary>
    /// Gets the status of the task.
    /// </summary>
    /// <returns>The task status.</returns>
    public virtual async Task<bool> IsCompletedAsync(PollingOptions options, CancellationToken cancellationToken = default)
    {
        var result = await PollAsyncCore(options, cancellationToken);
        return result.Status.IsCompleted();
    }

    /// <summary>
    /// Gets the status of the task.
    /// </summary>
    /// <returns>The task status.</returns>
    public virtual async Task<DurableTaskStatus> GetStatusAsync(PollingOptions options, CancellationToken cancellationToken = default)
    {
        var result = await PollAsyncCore(options, cancellationToken);
        return result.Status;
    }

    /// <summary>
    /// Gets the status of the task.
    /// </summary>
    /// <returns>The task status.</returns>
    public Task<bool> IsCompletedAsync(CancellationToken cancellationToken = default) => IsCompletedAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);

    /// <summary>
    /// Gets the status of the task.
    /// </summary>
    /// <returns>The task status.</returns>
    public Task<DurableTaskStatus> GetStatusAsync(CancellationToken cancellationToken = default) => GetStatusAsync(new PollingOptions { PollTimeout = TimeSpan.Zero }, cancellationToken);

    /// <summary>
    /// Waits for completion of this task and returns an object representing the result.
    /// </summary>
    /// <returns>The task status.</returns>
    public virtual async Task<DurableTaskResponse> GetResponseAsync(PollingOptions pollingOptions, CancellationToken cancellationToken = default)
    {
        return await PollAsyncCore(pollingOptions, cancellationToken);
    }

    /// <summary>
    /// Waits for completion of this task and returns an object representing the result.
    /// </summary>
    /// <returns>The task status.</returns>
    public virtual async Task<DurableTaskResponse> GetResponseAsync(CancellationToken cancellationToken = default)
    {
        return await WaitAsyncCore(cancellationToken);
    }

    /// <summary>
    /// Waits for the completion of this task.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    public virtual async ValueTask WaitAsync(CancellationToken cancellationToken = default) => await WaitAsyncCore(cancellationToken);

    /// <summary>
    /// Attempts to cancel the operation.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token used to signal when the attempt to request cancellation should be abandoned.</param>
    /// <returns>A task representing the completion of the operation.</returns>
    public abstract ValueTask CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a task representing the completion of the operation.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    protected internal abstract ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the current status of the task without waiting for the task to complete.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    protected abstract ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions pollingOptions, CancellationToken cancellationToken);

    public static async Task WhenAll(List<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        var innerTasks = new List<Task>(tasks.Count);
        foreach (var task in tasks)
        {
            innerTasks.Add(task.GetResponseAsync(cancellationToken));
        }

        await Task.WhenAll(innerTasks);
    }

    public static async Task WhenAll<TResult>(List<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
    {
        var innerTasks = new List<Task>(tasks.Count);
        foreach (var task in tasks)
        {
            innerTasks.Add(task.GetResponseAsync(cancellationToken));
        }

        await Task.WhenAll(innerTasks);
    }

    public static async Task<ScheduledTask> WhenAny(List<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        var completions = new List<Task>(tasks.Count);
        foreach (var task in tasks)
        {
            completions.Add(task.GetResponseAsync(cancellationToken));
        }

        var completed = await Task.WhenAny(completions);

        return tasks[completions.IndexOf(completed)];
    }

    public static async Task<ScheduledTask<TResult>> WhenAny<TResult>(List<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
    {
        var completions = new List<Task>(tasks.Count);
        foreach (var task in tasks)
        {
            completions.Add(task.GetResponseAsync(cancellationToken));
        }

        var completed = await Task.WhenAny(completions);

        return tasks[completions.IndexOf(completed)];
    }
}

/// <summary>
/// Represents an operation which is scheduled and will complete at an indefinite point in the future.
/// </summary>
public abstract class ScheduledTask<TResult> : ScheduledTask
{
    /// <summary>Gets an awaiter used to await this <see cref="ScheduledTask{TResult}"/>.</summary>
    /// <returns>An awaiter instance.</returns>
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this, cancellationToken: default);

    /// <summary>
    /// Waits for the completion of this task.
    /// </summary>
    /// <returns>A task representing the completion of the operation.</returns>
    public new virtual ConfiguredScheduledTaskAwaitable<TResult> WaitAsync(CancellationToken cancellationToken = default) => new(this, cancellationToken);
}

internal sealed class ScheduledDurableTask<TResult> : ScheduledTask<TResult>
{
    private readonly IScheduledTaskHandle _handle;

    internal ScheduledDurableTask(IScheduledTaskHandle handle)
    {
        _handle = handle;
    }

    public override TaskId Id => _handle.TaskId;

    public override async ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        await _handle.CancelAsync(cancellationToken);
    }

    protected override async ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions pollingOptions, CancellationToken cancellationToken)
    {
        return await _handle.PollAsync(pollingOptions, cancellationToken);
    }

    protected internal override async ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
    {
        return await _handle.WaitAsync(cancellationToken);
    }
}

internal sealed class CompletedScheduledDurableTask<TResult> : ScheduledTask<TResult>
{
    private readonly DurableTaskResponse _response;

    internal CompletedScheduledDurableTask(TaskId taskId, DurableTaskResponse response)
    {
        Id = taskId;
        _response = response;
    }

    public override TaskId Id { get; }

    public override ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions pollingOptions, CancellationToken cancellationToken) => new(_response);

    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => new(_response);
}

internal sealed class CompletedScheduledDurableTask : ScheduledTask
{
    private readonly DurableTaskResponse _response;

    internal CompletedScheduledDurableTask(TaskId taskId, DurableTaskResponse response)
    {
        Id = taskId;
        _response = response;
    }

    public override TaskId Id { get; }

    public override ValueTask CancelAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions pollingOptions, CancellationToken cancellationToken) => new(_response);

    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => new(_response);
}

internal sealed class ScheduledDurableTask : ScheduledTask
{
    private readonly IScheduledTaskHandle _handle;

    internal ScheduledDurableTask(IScheduledTaskHandle handle)
    {
        _handle = handle;
    }

    public override TaskId Id => _handle.TaskId;

    public override async ValueTask CancelAsync(CancellationToken cancellationToken)
    {
        await _handle.CancelAsync(cancellationToken);
    }

    protected override async ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions pollingOptions, CancellationToken cancellationToken)
    {
        return await _handle.PollAsync(pollingOptions, cancellationToken);
    }

    protected internal override async ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken)
    {
        return await _handle.WaitAsync(cancellationToken);
    }
}

/// <summary>
/// An awaiter for <see cref="ScheduledTask"/>.
/// </summary>
public readonly struct ScheduledTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly TaskAwaiter _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask durableTaskInvocation, CancellationToken cancellationToken) =>
        _awaiter = durableTaskInvocation.WaitAsync(cancellationToken).AsTask().GetAwaiter();

    /// <summary>
    /// Gets the result of the task.
    /// </summary>
    public void GetResult() => _awaiter.GetResult();

    /// <summary>
    /// Returns a value indicating whether the task has completed.
    /// </summary>
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}

/// <summary>
/// An awaiter for <see cref="ScheduledTask{TResult}"/>.
/// </summary>
/// <typeparam name="TResult">The underlying result type.</typeparam>
public readonly struct ScheduledTaskAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly TaskAwaiter<DurableTaskResponse> _awaiter;

    internal ScheduledTaskAwaiter(ScheduledTask<TResult> durableTaskInvocation, CancellationToken cancellationToken) =>
        _awaiter = durableTaskInvocation.WaitAsyncCore(cancellationToken).AsTask().GetAwaiter();

    /// <summary>
    /// Gets the result of the task.
    /// </summary>
    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();

    /// <summary>
    /// Returns a value indicating whether the task has completed.
    /// </summary>
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}

/// <summary>
/// An awaitable for <see cref="ScheduledTask{TResult}"/> which carries additional properties.
/// </summary>
/// <typeparam name="TResult">The underlying result type.</typeparam>
public readonly struct ConfiguredScheduledTaskAwaitable<TResult>
{
    private readonly ScheduledTask<TResult> scheduledTask;
    private readonly CancellationToken cancellationToken;

    internal ConfiguredScheduledTaskAwaitable(ScheduledTask<TResult> scheduledTask, CancellationToken cancellationToken)
    {
        this.scheduledTask = scheduledTask;
        this.cancellationToken = cancellationToken;
    }

    /// <summary>Gets an awaiter used to await this <see cref="ScheduledTask{TResult}"/>.</summary>
    /// <returns>An awaiter instance.</returns>
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(scheduledTask, cancellationToken: cancellationToken);

}

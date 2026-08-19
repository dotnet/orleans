using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/// <summary>Represents a host-scheduled durable task.</summary>
public abstract class ScheduledTask
{
    internal ScheduledTask() { }
    /// <summary>Gets the stable durable task identifier.</summary>
    public abstract TaskId Id { get; }
    /// <summary>Gets an awaiter which waits for successful completion.</summary>
    public ScheduledTaskAwaiter GetAwaiter() => new(this, CancellationToken.None);
    /// <summary>Polls whether the task has reached a terminal state.</summary>
    public async Task<bool> IsCompletedAsync(PollingOptions options = default, CancellationToken cancellationToken = default)
        => (await PollAsyncCore(options, cancellationToken)).IsCompleted;
    /// <summary>Polls the current task status.</summary>
    public async Task<DurableTaskStatus> GetStatusAsync(PollingOptions options = default, CancellationToken cancellationToken = default)
        => (await PollAsyncCore(options, cancellationToken)).Status;
    /// <summary>Waits for and returns the terminal response.</summary>
    public Task<DurableTaskResponse> GetResponseAsync(CancellationToken cancellationToken = default)
        => WaitAsyncCore(cancellationToken).AsTask();
    /// <summary>Polls and returns the current response.</summary>
    public Task<DurableTaskResponse> GetResponseAsync(PollingOptions options, CancellationToken cancellationToken = default)
        => PollAsyncCore(options, cancellationToken).AsTask();

    /// <summary>Waits for successful completion.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
        => (await WaitAsyncCore(cancellationToken)).EnsureSuccessfulCompletion();

    /// <summary>Durably requests cancellation.</summary>
    public abstract ValueTask CancelAsync(CancellationToken cancellationToken = default);
    /// <summary>Waits for a terminal host response.</summary>
    protected internal abstract ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken);
    /// <summary>Polls the current host response.</summary>
    protected abstract ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken);

    /// <summary>Waits for every scheduled task to complete successfully.</summary>
    public static async Task WhenAll(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
        => await Task.WhenAll(tasks.Select(task => task.WaitAsync(cancellationToken).AsTask()));

    /// <summary>Waits for every scheduled task to complete successfully.</summary>
    public static async Task WhenAll<TResult>(IReadOnlyList<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
        => await Task.WhenAll(tasks.Select(task => WaitForSuccessAsync(task, cancellationToken)));

    /// <summary>Returns the first scheduled task whose response completes.</summary>
    public static async Task<ScheduledTask> WhenAny(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Count);
        var waits = tasks.Select(task => task.GetResponseAsync(cancellationToken)).ToArray();
        var completed = await Task.WhenAny(waits);
        return tasks[Array.IndexOf(waits, completed)];
    }

    /// <summary>Returns the first scheduled task whose response completes.</summary>
    public static async Task<ScheduledTask<TResult>> WhenAny<TResult>(
        IReadOnlyList<ScheduledTask<TResult>> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Count);
        var waits = tasks.Select(task => task.GetResponseAsync(cancellationToken)).ToArray();
        var completed = await Task.WhenAny(waits);
        return tasks[Array.IndexOf(waits, completed)];
    }

    private static async Task WaitForSuccessAsync(ScheduledTask task, CancellationToken cancellationToken)
        => (await task.WaitAsyncCore(cancellationToken)).EnsureSuccessfulCompletion();
}

/// <summary>Represents a host-scheduled durable task with a result.</summary>
public abstract class ScheduledTask<TResult> : ScheduledTask
{
    /// <summary>Gets an awaiter which returns the successful result.</summary>
    public new ScheduledTaskAwaiter<TResult> GetAwaiter() => new(this, CancellationToken.None);
    /// <summary>Configures a typed wait with a wait-cancellation token.</summary>
    public new ConfiguredScheduledTaskAwaitable<TResult> WaitAsync(CancellationToken cancellationToken = default)
        => new(this, cancellationToken);
}

internal sealed class ScheduledTaskHandle(IScheduledTaskHandle handle) : ScheduledTask
{
    public override TaskId Id => handle.TaskId;
    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => handle.CancelAsync(cancellationToken);
    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => handle.WaitAsync(cancellationToken);
    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken)
        => handle.PollAsync(options, cancellationToken);
}

internal sealed class ScheduledTaskHandle<TResult>(IScheduledTaskHandle handle) : ScheduledTask<TResult>
{
    public override TaskId Id => handle.TaskId;
    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => handle.CancelAsync(cancellationToken);
    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => handle.WaitAsync(cancellationToken);
    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken)
        => handle.PollAsync(options, cancellationToken);
}

internal sealed class CompletedScheduledTask(TaskId id, DurableTaskResponse response) : ScheduledTask
{
    public override TaskId Id => id;
    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => new(response);
    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken) => new(response);
}

internal sealed class CompletedScheduledTask<TResult>(TaskId id, DurableTaskResponse response) : ScheduledTask<TResult>
{
    public override TaskId Id => id;
    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    protected internal override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) => new(response);
    protected override ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken) => new(response);
}

/// <summary>Provides an awaiter for a scheduled durable task without a result.</summary>
public readonly struct ScheduledTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _awaiter;
    internal ScheduledTaskAwaiter(ScheduledTask task, CancellationToken cancellationToken)
        => _awaiter = task.WaitAsync(cancellationToken).GetAwaiter();
    /// <inheritdoc />
    public bool IsCompleted => _awaiter.IsCompleted;
    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
    /// <summary>Waits for successful completion.</summary>
    public void GetResult() => _awaiter.GetResult();
}

/// <summary>Provides an awaiter for a scheduled durable task with a result.</summary>
public readonly struct ScheduledTaskAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<DurableTaskResponse> _awaiter;
    internal ScheduledTaskAwaiter(ScheduledTask<TResult> task, CancellationToken cancellationToken)
        => _awaiter = task.WaitAsyncCore(cancellationToken).GetAwaiter();
    /// <inheritdoc />
    public bool IsCompleted => _awaiter.IsCompleted;
    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
    /// <summary>Returns the successful result.</summary>
    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
}

/// <summary>Provides an awaitable scheduled durable task with a wait-cancellation token.</summary>
public readonly struct ConfiguredScheduledTaskAwaitable<TResult>(
    ScheduledTask<TResult> task,
    CancellationToken cancellationToken)
{
    /// <summary>Gets the configured awaiter.</summary>
    public ScheduledTaskAwaiter<TResult> GetAwaiter() => new(task, cancellationToken);
}

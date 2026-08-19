using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

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

    /// <summary>
    /// Returns the first scheduled task whose response completes, including failed or canceled durable
    /// responses. Wait cancellation and transport failures are propagated, and losing waits are canceled
    /// without canceling the durable tasks.
    /// </summary>
    public static async Task<ScheduledTask> WhenAny(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
        => tasks[await WaitForAnyIndexAsync(tasks, cancellationToken)];

    /// <summary>
    /// Returns the first scheduled task whose response completes, including failed or canceled durable
    /// responses. Wait cancellation and transport failures are propagated, and losing waits are canceled
    /// without canceling the durable tasks.
    /// </summary>
    public static async Task<ScheduledTask<TResult>> WhenAny<TResult>(
        IReadOnlyList<ScheduledTask<TResult>> tasks,
        CancellationToken cancellationToken = default)
        => tasks[await WaitForAnyIndexAsync(tasks, cancellationToken)];

    private static async Task<int> WaitForAnyIndexAsync<TTask>(
        IReadOnlyList<TTask> tasks,
        CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Count);
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waits = new List<Task<DurableTaskResponse>>(tasks.Count);
        try
        {
            foreach (var task in tasks)
            {
                waits.Add(task.WaitAsyncCore(waitCancellation.Token).AsTask());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAndDrainWaitsPreservingFailureAsync(waitCancellation, waits);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            await CancelAndDrainWaitsPreservingFailureAsync(waitCancellation, waits);
            throw;
        }

        var completed = await Task.WhenAny(waits);
        var winnerIndex = waits.IndexOf(completed);
        try
        {
            _ = await completed;
            return winnerIndex;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            await CancelAndDrainWaitsAsync(waitCancellation, waits, winnerIndex);
        }
    }

    private static async Task CancelAndDrainWaitsPreservingFailureAsync(
        CancellationTokenSource waitCancellation,
        IReadOnlyList<Task<DurableTaskResponse>> waits)
    {
        try
        {
            await CancelAndDrainWaitsAsync(waitCancellation, waits, winnerIndex: -1);
        }
        catch
        {
            // The failure which interrupted wait construction takes precedence over cleanup failures.
        }
    }

    private static async Task CancelAndDrainWaitsAsync(
        CancellationTokenSource waitCancellation,
        IReadOnlyList<Task<DurableTaskResponse>> waits,
        int winnerIndex)
    {
        Exception? cancellationException = null;
        try
        {
            waitCancellation.Cancel();
        }
        catch (Exception exception)
        {
            cancellationException = exception;
        }

        await DrainLosingWaitsAsync(waits, winnerIndex);
        if (cancellationException is not null)
        {
            ExceptionDispatchInfo.Capture(cancellationException).Throw();
        }
    }

    private static async Task DrainLosingWaitsAsync(
        IReadOnlyList<Task<DurableTaskResponse>> waits,
        int winnerIndex)
    {
        for (var index = 0; index < waits.Count; index++)
        {
            if (index == winnerIndex)
            {
                continue;
            }

            try
            {
                _ = await waits[index];
            }
            catch
            {
                // Losing observations are canceled and do not determine the result.
            }
        }
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

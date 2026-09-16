using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

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
        => (await PollAsyncCore(options, cancellationToken).ConfigureAwait(false)).IsCompleted;

    /// <summary>Polls the current task status.</summary>
    public async Task<DurableTaskStatus> GetStatusAsync(PollingOptions options = default, CancellationToken cancellationToken = default)
        => (await PollAsyncCore(options, cancellationToken).ConfigureAwait(false)).Status;

    /// <summary>Waits for and returns the terminal response.</summary>
    public Task<DurableTaskResponse> GetResponseAsync(CancellationToken cancellationToken = default)
        => WaitAsyncCore(cancellationToken).AsTask();

    /// <summary>Polls and returns the current response.</summary>
    public Task<DurableTaskResponse> GetResponseAsync(PollingOptions options, CancellationToken cancellationToken = default)
        => PollAsyncCore(options, cancellationToken).AsTask();

    /// <summary>Waits for successful completion.</summary>
    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
        => (await WaitAsyncCore(cancellationToken).ConfigureAwait(false)).EnsureSuccessfulCompletion();

    /// <summary>Durably requests cancellation.</summary>
    public abstract ValueTask CancelAsync(CancellationToken cancellationToken = default);

    /// <summary>Waits for a terminal host response.</summary>
    protected internal abstract ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken);

    /// <summary>Polls the current host response.</summary>
    protected abstract ValueTask<DurableTaskResponse> PollAsyncCore(PollingOptions options, CancellationToken cancellationToken);

    /// <summary>Waits for every scheduled task to complete successfully.</summary>
    public static async Task WhenAll(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        await Task.WhenAll(tasks.Select(task => task.WaitAsync(cancellationToken).AsTask())).ConfigureAwait(false);
    }

    /// <summary>Waits for every scheduled task to complete successfully.</summary>
    public static async Task WhenAll<TResult>(IReadOnlyList<ScheduledTask<TResult>> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        await Task.WhenAll(tasks.Select(task => WaitForSuccessAsync(task, cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the first scheduled task whose response completes, including failed or canceled durable
    /// responses. Wait cancellation and transport failures are propagated, and losing waits are canceled
    /// without canceling the durable tasks. Candidates are captured when this method is called.
    /// </summary>
    public static async Task<ScheduledTask> WhenAny(IReadOnlyList<ScheduledTask> tasks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        return candidates[await WaitForAnyIndexAsync(candidates, cancellationToken).ConfigureAwait(false)];
    }

    /// <summary>
    /// Returns the first scheduled task whose response completes, including failed or canceled durable
    /// responses. Wait cancellation and transport failures are propagated, and losing waits are canceled
    /// without canceling the durable tasks. Candidates are captured when this method is called.
    /// </summary>
    public static async Task<ScheduledTask<TResult>> WhenAny<TResult>(
        IReadOnlyList<ScheduledTask<TResult>> tasks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var candidates = tasks.ToArray();
        return candidates[await WaitForAnyIndexAsync(candidates, cancellationToken).ConfigureAwait(false)];
    }

    private static async Task<int> WaitForAnyIndexAsync<TTask>(
        IReadOnlyList<TTask> tasks,
        CancellationToken cancellationToken)
        where TTask : ScheduledTask
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Count, nameof(tasks));
        using var waitCancellation = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();
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
            await CancelAndDrainWaitsPreservingFailureAsync(waitCancellation, waits).ConfigureAwait(false);
            throw new OperationCanceledException(cancellationToken);
        }
        catch
        {
            await CancelAndDrainWaitsPreservingFailureAsync(waitCancellation, waits).ConfigureAwait(false);
            throw;
        }

        var completed = await Task.WhenAny(waits).ConfigureAwait(false);
        var winnerIndex = waits.IndexOf(completed);
        try
        {
            _ = await completed.ConfigureAwait(false);
            return winnerIndex;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        finally
        {
            await CancelAndDrainWaitsAsync(waitCancellation, waits, winnerIndex).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The original wait-construction failure takes precedence over best-effort cleanup failures.")]
    private static async Task CancelAndDrainWaitsPreservingFailureAsync(
        CancellationTokenSource waitCancellation,
        IReadOnlyList<Task<DurableTaskResponse>> waits)
    {
        try
        {
            await CancelAndDrainWaitsAsync(waitCancellation, waits, winnerIndex: -1).ConfigureAwait(false);
        }
        catch
        {
            // The failure which interrupted wait construction takes precedence over cleanup failures.
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Canceling losing wait observations is best-effort cleanup.")]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "CancellationTokenSource.Cancel is used to synchronously notify every losing wait before they are drained.")]
    private static async Task CancelAndDrainWaitsAsync(
        CancellationTokenSource waitCancellation,
        IReadOnlyList<Task<DurableTaskResponse>> waits,
        int winnerIndex)
    {
        try
        {
            waitCancellation.Cancel();
        }
        catch
        {
            // Canceling losing wait observations is best-effort cleanup.
        }

        await DrainLosingWaitsAsync(waits, winnerIndex).ConfigureAwait(false);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Losing observation failures do not determine the WhenAny result.")]
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
                _ = await waits[index].ConfigureAwait(false);
            }
            catch
            {
                // Losing observations are canceled and do not determine the result.
            }
        }
    }

    private static async Task WaitForSuccessAsync(ScheduledTask task, CancellationToken cancellationToken)
        => (await task.WaitAsyncCore(cancellationToken).ConfigureAwait(false)).EnsureSuccessfulCompletion();
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
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Awaiters are compiler protocol values whose equality is not part of their contract.")]
public readonly struct ScheduledTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter _awaiter;

    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "The compiler consumes this awaiter once, and the stored awaiter preserves allocation-free IValueTaskSource implementations.")]
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
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Awaiters are compiler protocol values whose equality is not part of their contract.")]
public readonly struct ScheduledTaskAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly ValueTaskAwaiter<DurableTaskResponse> _awaiter;

    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "The compiler consumes this awaiter once, and the stored awaiter preserves allocation-free IValueTaskSource implementations.")]
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
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Configured awaitables are compiler protocol values whose equality is not part of their contract.")]
public readonly struct ConfiguredScheduledTaskAwaitable<TResult>(
    ScheduledTask<TResult> task,
    CancellationToken cancellationToken)
{
    /// <summary>Gets the configured awaiter.</summary>
    public ScheduledTaskAwaiter<TResult> GetAwaiter() => new(task, cancellationToken);
}

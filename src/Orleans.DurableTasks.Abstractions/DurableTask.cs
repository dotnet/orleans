using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>Defines a deferred, host-scheduled durable asynchronous operation.</summary>
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract class DurableTask
{
    /// <summary>Creates a successfully completed durable task.</summary>
    public static DurableTask<TResult> FromResult<TResult>(TResult value) => new CompletedDurableTask<TResult>(value);

    /// <summary>Creates a durable delay measured using host logical time.</summary>
    public static DurableTask Delay(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        return new DelayDurableTask(duration);
    }

    /// <summary>Creates a durable delay which completes at <paramref name="dueTime"/>, measured using host logical time.</summary>
    /// <remarks>A due time at or before the current logical time completes the delay promptly.</remarks>
    public static DurableTask DelayUntil(DateTimeOffset dueTime) => new DelayUntilDurableTask(dueTime);

    /// <summary>Creates a durable task which invokes <paramref name="action"/>.</summary>
    public static DurableTask Run(Action<CancellationToken> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new DelegateDurableTask(action);
    }

    /// <summary>Creates a durable task which invokes <paramref name="function"/>.</summary>
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new DelegateDurableTask<TResult>(function);
    }

    /// <summary>Creates a durable task which invokes asynchronous <paramref name="function"/>.</summary>
    public static DurableTask Run(Func<CancellationToken, Task> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask(function);
    }

    /// <summary>Creates a durable task which invokes asynchronous <paramref name="function"/>.</summary>
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, Task<TResult>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask<TResult>(function);
    }

    /// <summary>Creates a durable task which invokes <paramref name="action"/> with captured <paramref name="state"/>.</summary>
    public static DurableTask Run<TState>(Action<TState, CancellationToken> action, TState state)
    {
        ArgumentNullException.ThrowIfNull(action);
        return Run(ct => action(state, ct));
    }

    /// <summary>Creates a durable task which invokes <paramref name="function"/> with captured <paramref name="state"/>.</summary>
    public static DurableTask<TResult> Run<TState, TResult>(Func<TState, CancellationToken, TResult> function, TState state)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Run(ct => function(state, ct));
    }

    /// <summary>Creates a durable task which invokes asynchronous <paramref name="function"/> with captured <paramref name="state"/>.</summary>
    public static DurableTask Run<TState>(Func<TState, CancellationToken, Task> function, TState state)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Run(ct => function(state, ct));
    }

    /// <summary>Creates a durable task which invokes asynchronous <paramref name="function"/> with captured <paramref name="state"/>.</summary>
    public static DurableTask<TResult> Run<TState, TResult>(Func<TState, CancellationToken, Task<TResult>> function, TState state)
    {
        ArgumentNullException.ThrowIfNull(function);
        return Run(ct => function(state, ct));
    }

    /// <summary>Creates a durable task which completes after every input task completes successfully.</summary>
    /// <remarks>A failed or canceled input fails the combined task.</remarks>
    public static DurableTask WhenAll(IReadOnlyList<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask>>(
            WhenAllCore,
            tasks.ToArray());
    }

    /// <summary>Creates a durable task which returns the results of every input task, in input order.</summary>
    /// <remarks>A failed or canceled input fails the combined task.</remarks>
    public static DurableTask<IReadOnlyList<TResult>> WhenAll<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask<TResult>>, IReadOnlyList<TResult>>(
            WhenAllCore<TResult>,
            tasks.ToArray());
    }

    /// <summary>Creates a durable task which returns the input index of the first input task to complete.</summary>
    /// <remarks>
    /// The host records the winner, so replay selects the same input. A winner which failed or was canceled fails the
    /// combined task. The other inputs keep running.
    /// </remarks>
    public static DurableTask<int> WhenAny(IReadOnlyList<DurableTask> tasks)
    {
        ValidateWhenAnyTasks(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask>, int>(
            WhenAnyCore,
            tasks.ToArray());
    }

    /// <summary>Creates a durable task which returns the input index and result of the first input task to complete.</summary>
    /// <remarks>
    /// The host records the winner, so replay selects the same input. A winner which failed or was canceled fails the
    /// combined task. The other inputs keep running.
    /// </remarks>
    public static DurableTask<(int Index, TResult Result)> WhenAny<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ValidateWhenAnyTasks(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask<TResult>>, (int Index, TResult Result)>(
            WhenAnyCore<TResult>,
            tasks.ToArray());
    }

    private static async Task WhenAllCore(
        IReadOnlyList<DurableTask> tasks,
        CancellationToken _)
    {
        // Cancellation reaches the inputs, which are attached children, so the combinator waits for their outcomes
        // and stops waiting only when the host aborts the attempt.
        var cancellationToken = GetCurrentContext(nameof(WhenAll)).ExecutionAbortToken;
        var scheduled = await ScheduleCombinatorInputsAsync(tasks, nameof(WhenAll), "when-all", cancellationToken).ConfigureAwait(false);
        await ScheduledTask.WhenAll(scheduled.Tasks, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TResult>> WhenAllCore<TResult>(
        IReadOnlyList<DurableTask<TResult>> tasks,
        CancellationToken _)
    {
        // Cancellation reaches the inputs, which are attached children, so the combinator waits for their outcomes
        // and stops waiting only when the host aborts the attempt.
        var cancellationToken = GetCurrentContext(nameof(WhenAll)).ExecutionAbortToken;
        var scheduled = await ScheduleCombinatorInputsAsync(tasks, nameof(WhenAll), "when-all", cancellationToken).ConfigureAwait(false);
        await ScheduledTask.WhenAll(scheduled.Tasks, cancellationToken).ConfigureAwait(false);
        var results = new TResult[scheduled.Tasks.Length];
        for (var index = 0; index < results.Length; index++)
        {
            results[index] = (await scheduled.Tasks[index].GetResponseAsync(cancellationToken).ConfigureAwait(false)).GetResult<TResult>();
        }

        return results;
    }

    private static async Task<int> WhenAnyCore(
        IReadOnlyList<DurableTask> tasks,
        CancellationToken _)
    {
        // Cancellation reaches the inputs, which are attached children, so the combinator waits for their outcomes
        // and stops waiting only when the host aborts the attempt.
        var cancellationToken = GetCurrentContext(nameof(WhenAny)).ExecutionAbortToken;
        var scheduled = await ScheduleCombinatorInputsAsync(tasks, nameof(WhenAny), "when-any", cancellationToken).ConfigureAwait(false);
        var index = await SelectWinnerAsync(scheduled.Context, scheduled.OperationId, scheduled.Tasks, cancellationToken).ConfigureAwait(false);
        await scheduled.Tasks[index].WaitAsync(cancellationToken).ConfigureAwait(false);
        return index;
    }

    private static async Task<(int Index, TResult Result)> WhenAnyCore<TResult>(
        IReadOnlyList<DurableTask<TResult>> tasks,
        CancellationToken _)
    {
        // Cancellation reaches the inputs, which are attached children, so the combinator waits for their outcomes
        // and stops waiting only when the host aborts the attempt.
        var cancellationToken = GetCurrentContext(nameof(WhenAny)).ExecutionAbortToken;
        var scheduled = await ScheduleCombinatorInputsAsync(tasks, nameof(WhenAny), "when-any", cancellationToken).ConfigureAwait(false);
        var index = await SelectWinnerAsync(scheduled.Context, scheduled.OperationId, scheduled.Tasks, cancellationToken).ConfigureAwait(false);
        return (index, (await scheduled.Tasks[index].GetResponseAsync(cancellationToken).ConfigureAwait(false)).GetResult<TResult>());
    }

    private static async Task<(DurableExecutionContext Context, TaskId OperationId, ScheduledTask[] Tasks)> ScheduleCombinatorInputsAsync(
        IReadOnlyList<DurableTask> tasks,
        string operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext(operation);
        var operationId = context.CreateOperationId(operationName);

        // Inputs are scheduled concurrently, so a host can commit the whole batch together. Their indexed identifiers
        // keep them replay-stable in any scheduling order.
        var scheduling = new Task<ScheduledTask>[tasks.Count];
        for (var index = 0; index < tasks.Count; index++)
        {
            scheduling[index] = new ConfiguredDurableTask(tasks[index], CreateCombinatorChildId(operationId, index))
                .ScheduleAsync(cancellationToken);
        }

        return (context, operationId, await Task.WhenAll(scheduling).ConfigureAwait(false));
    }

    private static async Task<(DurableExecutionContext Context, TaskId OperationId, ScheduledTask<TResult>[] Tasks)> ScheduleCombinatorInputsAsync<TResult>(
        IReadOnlyList<DurableTask<TResult>> tasks,
        string operation,
        string operationName,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext(operation);
        var operationId = context.CreateOperationId(operationName);
        var scheduling = new Task<ScheduledTask<TResult>>[tasks.Count];
        for (var index = 0; index < tasks.Count; index++)
        {
            scheduling[index] = new ConfiguredDurableTask<TResult>(
                tasks[index],
                CreateCombinatorChildId(operationId, index)).ScheduleAsync(cancellationToken);
        }

        return (context, operationId, await Task.WhenAll(scheduling).ConfigureAwait(false));
    }

    private static async Task<int> SelectWinnerAsync(
        DurableExecutionContext context,
        TaskId operationId,
        ScheduledTask[] scheduled,
        CancellationToken cancellationToken)
    {
        var candidates = new TaskId[scheduled.Length];
        for (var index = 0; index < candidates.Length; index++)
        {
            candidates[index] = scheduled[index].Id;
        }

        var winner = await context.SelectCompletionAsync(operationId.Child("$winner"), candidates, cancellationToken).ConfigureAwait(false);
        var winnerIndex = Array.IndexOf(candidates, winner);
        if (winnerIndex < 0)
        {
            throw new InvalidOperationException($"The host selected '{winner}', which is not a candidate of '{operationId}'.");
        }

        return winnerIndex;
    }
    private static DurableExecutionContext GetCurrentContext(string operation)
        => DurableExecutionContext.Current
            ?? throw new InvalidOperationException($"DurableTask.{operation} requires a durable execution context.");

    private static TaskId CreateCombinatorChildId(TaskId operationId, int index)
        => operationId.Child(index.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static void ValidateWhenAnyTasks<TTask>(IReadOnlyList<TTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentOutOfRangeException.ThrowIfZero(tasks.Count, nameof(tasks));
    }

    /// <summary>Runs the definition in the supplied execution context.</summary>
    protected internal abstract ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context);

    internal virtual bool RunsInlineInParent => false;
}

/// <summary>Defines a deferred durable asynchronous operation with a result.</summary>
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask;

internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => new(DurableTaskResponse.FromResult(value));
}

internal sealed class DelayDurableTask(TimeSpan duration) : DurableTask
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => context.ScheduleDelayAsync(context.TaskId, context.UtcNow + duration, context.CancellationToken);
}

internal sealed class DelayUntilDurableTask(DateTimeOffset dueTime) : DurableTask
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => context.ScheduleDelayAsync(context.TaskId, dueTime, context.CancellationToken);
}

internal abstract class DelegateDurableTaskBase : DurableTask
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    protected async ValueTask<DurableTaskResponse> InvokeAsync(
        DurableExecutionContext context,
        Func<CancellationToken, ValueTask<DurableTaskResponse>> callback)
    {
        using var scope = DurableExecutionContext.Enter(context);

        try
        {
            return await callback(context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

internal sealed class DelegateDurableTask(Action<CancellationToken> action) : DelegateDurableTaskBase
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => InvokeAsync(context, token =>
        {
            action(token);
            return new(DurableTaskResponse.Completed);
        });
}

internal sealed class DelegateDurableTask<TResult>(Func<CancellationToken, TResult> function) : DurableTask<TResult>
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        using var scope = DurableExecutionContext.Enter(context);

        try
        {
            return new(DurableTaskResponse.FromResult(function(context.CancellationToken)));
        }
        catch (Exception exception)
        {
            return new(DurableTaskResponse.FromException(exception));
        }
    }
}

internal class AsyncDelegateDurableTask(Func<CancellationToken, Task> function) : DelegateDurableTaskBase
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
        => InvokeAsync(context, async token =>
        {
            await function(token).ConfigureAwait(false);
            return DurableTaskResponse.Completed;
        });
}

internal class AsyncDelegateDurableTask<TResult>(Func<CancellationToken, Task<TResult>> function) : DurableTask<TResult>
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    protected internal override async ValueTask<DurableTaskResponse> RunAsync(DurableExecutionContext context)
    {
        using var scope = DurableExecutionContext.Enter(context);

        try
        {
            return DurableTaskResponse.FromResult(await function(context.CancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }
}

internal sealed class InlineAsyncDelegateDurableTask<TState>(
    Func<TState, CancellationToken, Task> function,
    TState state) : AsyncDelegateDurableTask(token => function(state, token))
{
    internal override bool RunsInlineInParent => true;
}

internal sealed class InlineAsyncDelegateDurableTask<TState, TResult>(
    Func<TState, CancellationToken, Task<TResult>> function,
    TState state) : AsyncDelegateDurableTask<TResult>(token => function(state, token))
{
    internal override bool RunsInlineInParent => true;
}

internal struct ConfiguredDurableTaskCore<TTask> where TTask : DurableTask
{
    private TaskId _taskId;

    internal ConfiguredDurableTaskCore(TTask task) : this(task, DurableExecutionContext.Current)
    {
    }

    internal ConfiguredDurableTaskCore(TTask task, DurableExecutionContext? parentContext)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        ParentContext = parentContext;
    }

    internal TTask Task { get; }

    internal DurableExecutionContext? ParentContext { get; }

    internal readonly TaskId TaskId => _taskId;

    internal void SetId(string segment)
    {
        if (!_taskId.IsDefault)
        {
            throw new InvalidOperationException("The durable task identifier has already been specified.");
        }

        _taskId = ParentContext is { } parent ? parent.CreateChildTaskId(segment) : TaskId.CreateRoot(segment);
    }

    internal void SetId(TaskId taskId)
    {
        if (!_taskId.IsDefault)
        {
            throw new InvalidOperationException("The durable task identifier has already been specified.");
        }

        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable task identifier cannot be empty.", nameof(taskId));
        }

        _taskId = taskId;
    }

    private void EnsureId()
    {
        if (!_taskId.IsDefault)
        {
            return;
        }

        _taskId = ParentContext is { } parent
            ? parent.CreateChildTaskId(null)
            : throw new InvalidOperationException("Root durable tasks require an explicit stable identifier.");
    }

    internal async Task<DurableTaskResponse> RunAsync(CancellationToken cancellationToken)
    {
        if (_taskId.IsDefault && ParentContext is { } parent && Task.RunsInlineInParent)
        {
            return await Task.RunAsync(parent).ConfigureAwait(false);
        }

        var scheduled = await ScheduleCoreAsync(cancellationToken).ConfigureAwait(false);
        return scheduled.Response ?? await scheduled.Handle!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<(DurableTaskResponse? Response, IScheduledTaskHandle? Handle)> ScheduleCoreAsync(
        CancellationToken cancellationToken)
    {
        EnsureId();
        if (ParentContext is { } parent)
        {
            return (null, await parent.ScheduleChildTaskAsync(_taskId, Task, cancellationToken).ConfigureAwait(false));
        }

        if (Task is not ISchedulableTask schedulable)
        {
            throw new InvalidOperationException("The durable task definition does not support root scheduling.");
        }

        var response = await schedulable.ScheduleAsync(_taskId, cancellationToken).ConfigureAwait(false);
        return response.IsCompleted
            ? (response, null)
            : (null, schedulable.GetHandle(_taskId));
    }

    internal IScheduledTaskHandle GetHandle()
    {
        EnsureId();
        if (ParentContext is { } parent)
        {
            return parent.GetChildTaskHandle(_taskId);
        }

        return Task is ISchedulableTask schedulable
            ? schedulable.GetHandle(_taskId)
            : throw new InvalidOperationException("The durable task definition does not support root scheduling.");
    }
}

/// <summary>Configures and schedules a durable task.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "This mutable fluent configuration value has no equality contract.")]
public struct ConfiguredDurableTask
{
    private ConfiguredDurableTaskCore<DurableTask> _core;

    internal ConfiguredDurableTask(DurableTask task) => _core = new(task);

    internal ConfiguredDurableTask(DurableTask task, string rootId)
    {
        _core = new(task, parentContext: null);
        _core.SetId(TaskId.CreateRoot(rootId));
    }

    internal ConfiguredDurableTask(DurableTask task, TaskId taskId)
    {
        _core = new(task);
        _core.SetId(taskId);
    }

    /// <summary>Gets an awaiter which runs or attaches to the task.</summary>
    public DurableTaskAwaiter GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    /// <summary>
    /// Assigns a stable logical identifier segment. Within a durable execution, segments beginning
    /// with <c>$</c> are reserved for generated identifiers.
    /// </summary>
    public ConfiguredDurableTask WithId(string segment) { _core.SetId(segment); return this; }

    /// <summary>Schedules the configured definition and returns its handle.</summary>
    public async Task<ScheduledTask> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        var scheduled = await _core.ScheduleCoreAsync(cancellationToken).ConfigureAwait(false);
        return scheduled.Response is { } response
            ? new CompletedScheduledTask(_core.TaskId, response)
            : new ScheduledTaskHandle(scheduled.Handle!);
    }

    /// <summary>Requests durable cancellation.</summary>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
        => _core.GetHandle().CancelAsync(cancellationToken);

    /// <summary>Polls the task and returns its current status.</summary>
    public async Task<DurableTaskStatus> PollAsync(
        PollingOptions options = default,
        CancellationToken cancellationToken = default)
        => (await _core.GetHandle().PollAsync(options, cancellationToken).ConfigureAwait(false)).Status;
}

/// <summary>Configures and schedules a durable task with a result.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "This mutable fluent configuration value has no equality contract.")]
public struct ConfiguredDurableTask<TResult>
{
    private ConfiguredDurableTaskCore<DurableTask<TResult>> _core;

    internal ConfiguredDurableTask(DurableTask<TResult> task) => _core = new(task);

    internal ConfiguredDurableTask(DurableTask<TResult> task, string rootId)
    {
        _core = new(task, parentContext: null);
        _core.SetId(TaskId.CreateRoot(rootId));
    }

    internal ConfiguredDurableTask(DurableTask<TResult> task, TaskId taskId)
    {
        _core = new(task);
        _core.SetId(taskId);
    }

    /// <summary>Gets an awaiter which runs or attaches to the task.</summary>
    public DurableTaskAwaiter<TResult> GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    /// <summary>
    /// Assigns a stable logical identifier segment. Within a durable execution, segments beginning
    /// with <c>$</c> are reserved for generated identifiers.
    /// </summary>
    public ConfiguredDurableTask<TResult> WithId(string segment) { _core.SetId(segment); return this; }

    /// <summary>Schedules the configured definition and returns its typed handle.</summary>
    public async Task<ScheduledTask<TResult>> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        var scheduled = await _core.ScheduleCoreAsync(cancellationToken).ConfigureAwait(false);
        return scheduled.Response is { } response
            ? new CompletedScheduledTask<TResult>(_core.TaskId, response)
            : new ScheduledTaskHandle<TResult>(scheduled.Handle!);
    }

    /// <summary>Requests durable cancellation.</summary>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
        => _core.GetHandle().CancelAsync(cancellationToken);

    /// <summary>Polls the task and returns its current status.</summary>
    public async Task<DurableTaskStatus> PollAsync(
        PollingOptions options = default,
        CancellationToken cancellationToken = default)
        => (await _core.GetHandle().PollAsync(options, cancellationToken).ConfigureAwait(false)).Status;
}

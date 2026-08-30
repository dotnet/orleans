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

    /// <summary>Creates a durable task which completes after every input task and returns their stable identifiers.</summary>
    public static DurableTask<IReadOnlyList<TaskId>> WhenAll(IReadOnlyList<DurableTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask>, IReadOnlyList<TaskId>>(
            WhenAllCore,
            tasks.ToArray());
    }

    /// <summary>Creates a durable task which completes after every input task and returns their stable identifiers.</summary>
    public static DurableTask<IReadOnlyList<TaskId>> WhenAll<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask<TResult>>, IReadOnlyList<TaskId>>(
            WhenAllCore<TResult>,
            tasks.ToArray());
    }

    /// <summary>Creates a durable task which returns the replay-stable identifier of the first completed input.</summary>
    public static DurableTask<TaskId> WhenAny(IReadOnlyList<DurableTask> tasks)
    {
        ValidateWhenAnyTasks(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask>, TaskId>(
            WhenAnyCore,
            tasks.ToArray());
    }

    /// <summary>Creates a durable task which returns the replay-stable identifier of the first completed input.</summary>
    public static DurableTask<TaskId> WhenAny<TResult>(IReadOnlyList<DurableTask<TResult>> tasks)
    {
        ValidateWhenAnyTasks(tasks);
        return new InlineAsyncDelegateDurableTask<IReadOnlyList<DurableTask<TResult>>, TaskId>(
            WhenAnyCore<TResult>,
            tasks.ToArray());
    }

    private static async Task<IReadOnlyList<TaskId>> WhenAllCore(
        IReadOnlyList<DurableTask> tasks,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext(nameof(WhenAll));
        var operationId = context.CreateOperationId("when-all");
        var scheduled = new ScheduledTask[tasks.Count];
        for (var index = 0; index < tasks.Count; index++)
        {
            scheduled[index] = await new ConfiguredDurableTask(tasks[index], CreateCombinatorChildId(operationId, index))
                .ScheduleCombinatorChildAsync(cancellationToken).ConfigureAwait(false);
        }

        await ScheduledTask.WhenAll(scheduled, cancellationToken).ConfigureAwait(false);
        return scheduled.Select(task => task.Id).ToArray();
    }

    private static async Task<IReadOnlyList<TaskId>> WhenAllCore<TResult>(
        IReadOnlyList<DurableTask<TResult>> tasks,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext(nameof(WhenAll));
        var operationId = context.CreateOperationId("when-all");
        var scheduled = new ScheduledTask<TResult>[tasks.Count];
        for (var index = 0; index < tasks.Count; index++)
        {
            scheduled[index] = await new ConfiguredDurableTask<TResult>(
                tasks[index],
                CreateCombinatorChildId(operationId, index)).ScheduleCombinatorChildAsync(cancellationToken).ConfigureAwait(false);
        }

        await ScheduledTask.WhenAll(scheduled, cancellationToken).ConfigureAwait(false);
        return scheduled.Select(task => task.Id).ToArray();
    }

    private static async Task<TaskId> WhenAnyCore(
        IReadOnlyList<DurableTask> tasks,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext(nameof(WhenAny));
        var operationId = context.CreateOperationId("when-any");
        var scheduled = new ScheduledTask[tasks.Count];
        for (var index = 0; index < tasks.Count; index++)
        {
            scheduled[index] = await new ConfiguredDurableTask(tasks[index], CreateCombinatorChildId(operationId, index))
                .ScheduleCombinatorChildAsync(cancellationToken).ConfigureAwait(false);
        }

        var winner = await context.SelectCompletionAsync(
            operationId.Child("$winner"),
            scheduled.Select(task => task.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < scheduled.Length; index++)
        {
            if (scheduled[index].Id != winner && tasks[index] is not ISchedulableTask)
            {
                await scheduled[index].CancelAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return winner;
    }

    private static async Task<TaskId> WhenAnyCore<TResult>(
        IReadOnlyList<DurableTask<TResult>> tasks,
        CancellationToken cancellationToken)
    {
        var context = GetCurrentContext(nameof(WhenAny));
        var operationId = context.CreateOperationId("when-any");
        var scheduled = new ScheduledTask<TResult>[tasks.Count];
        for (var index = 0; index < tasks.Count; index++)
        {
            scheduled[index] = await new ConfiguredDurableTask<TResult>(
                tasks[index],
                CreateCombinatorChildId(operationId, index)).ScheduleCombinatorChildAsync(cancellationToken).ConfigureAwait(false);
        }

        var winner = await context.SelectCompletionAsync(
            operationId.Child("$winner"),
            scheduled.Select(task => task.Id).ToArray(),
            cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < scheduled.Length; index++)
        {
            if (scheduled[index].Id != winner && tasks[index] is not ISchedulableTask)
            {
                await scheduled[index].CancelAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return winner;
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
        => context.ScheduleDelayAsync(context.TaskId, duration, context.CancellationToken);
}

internal abstract class DelegateDurableTaskBase : DurableTask
{
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

internal sealed class AsyncDelegateDurableTask(Func<CancellationToken, Task> function) : DelegateDurableTaskBase
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

        var scheduled = await ScheduleCoreAsync(
            cancellationToken,
            allowLocalChild: true).ConfigureAwait(false);
        return scheduled.Response ?? await scheduled.Handle!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<(DurableTaskResponse? Response, IScheduledTaskHandle? Handle)> ScheduleCoreAsync(
        CancellationToken cancellationToken,
        bool allowLocalChild = false)
    {
        EnsureId();
        if (ParentContext is { } parent)
        {
            if (!allowLocalChild && Task is not ISchedulableTask)
            {
                throw new NotSupportedException(
                    "Detached scheduling of local durable tasks is not recoverable. Await the task directly or schedule a host-backed durable task.");
            }

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

    internal async Task<ScheduledTask> ScheduleCombinatorChildAsync(CancellationToken cancellationToken)
    {
        var scheduled = await _core.ScheduleCoreAsync(cancellationToken, allowLocalChild: true).ConfigureAwait(false);
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

    internal async Task<ScheduledTask<TResult>> ScheduleCombinatorChildAsync(CancellationToken cancellationToken)
    {
        var scheduled = await _core.ScheduleCoreAsync(cancellationToken, allowLocalChild: true).ConfigureAwait(false);
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

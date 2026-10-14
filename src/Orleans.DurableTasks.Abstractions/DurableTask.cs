using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>Defines a deferred, host-scheduled durable asynchronous operation.</summary>
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract partial class DurableTask
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
        if (ParentContext is { } parent)
        {
            // An awaited combinator runs in the parent, and an identifier assigned with WithId names its operation.
            if (Task is IInlineOperation operation)
            {
                return await operation.RunInlineAsync(parent, _taskId).ConfigureAwait(false);
            }

            if (_taskId.IsDefault && Task.RunsInlineInParent)
            {
                return await Task.RunAsync(parent).ConfigureAwait(false);
            }
        }

        var scheduled = await ScheduleCoreAsync(cancellationToken).ConfigureAwait(false);
        return scheduled.Response ?? await scheduled.Handle!.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<(DurableTaskResponse? Response, IScheduledTaskHandle? Handle)> ScheduleCoreAsync(
        CancellationToken cancellationToken)
    {
        if (Task is IInlineOperation { InlineOnly: true })
        {
            throw IInlineOperation.CreateInlineOnlyException();
        }

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

    /// <summary>Requests durable cancellation carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableExecutionContext.CancellationReason"/> reports, or
    /// <see langword="null"/> for none. The first request determines the reason.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken = default)
        => _core.GetHandle().CancelAsync(reason, cancellationToken);

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

    /// <summary>Requests durable cancellation carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableExecutionContext.CancellationReason"/> reports, or
    /// <see langword="null"/> for none. The first request determines the reason.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken = default)
        => _core.GetHandle().CancelAsync(reason, cancellationToken);

    /// <summary>Polls the task and returns its current status.</summary>
    public async Task<DurableTaskStatus> PollAsync(
        PollingOptions options = default,
        CancellationToken cancellationToken = default)
        => (await _core.GetHandle().PollAsync(options, cancellationToken).ConfigureAwait(false)).Status;
}

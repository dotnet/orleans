using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>Defines a deferred, host-scheduled durable asynchronous operation.</summary>
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder))]
public abstract partial class DurableTask
{
    /// <summary>Gets the context of the durable task which the calling code runs in.</summary>
    /// <remarks>
    /// The context describes the running task: its identifier, logical time, and cancellation. Code which also runs
    /// outside durable tasks checks with <see cref="DurableTaskContext.TryGetCurrent"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The calling code isn't running in a durable task.</exception>
    public static DurableTaskContext Current => DurableTaskContext.Ambient
        ?? throw new InvalidOperationException("The calling code isn't running in a durable task.");

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
    protected internal abstract ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context);

    internal virtual bool RunsInlineInParent => false;
}

/// <summary>Defines a deferred durable asynchronous operation with a result.</summary>
[AsyncMethodBuilder(typeof(DurableTaskMethodBuilder<>))]
public abstract class DurableTask<TResult> : DurableTask;

internal sealed class CompletedDurableTask<TResult>(TResult value) : DurableTask<TResult>
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        => new(DurableTaskResponse.FromResult(value));
}

internal sealed class DelayDurableTask(TimeSpan duration) : DurableTask
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        => context.ScheduleDelayAsync(context.Id, context.UtcNow + duration, context.CancellationToken);
}

internal sealed class DelayUntilDurableTask(DateTimeOffset dueTime) : DurableTask
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        => context.ScheduleDelayAsync(context.Id, dueTime, context.CancellationToken);
}

/// <summary>Runs delegate bodies and reports their completion to the execution context.</summary>
internal static class DelegateBody
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    public static ValueTask<DurableTaskResponse> Run<TState>(
        DurableTaskContext context,
        Func<TState, CancellationToken, DurableTaskResponse> body,
        TState state)
    {
        DurableTaskResponse response;
        using (DurableTaskContext.Enter(context))
        {
            try
            {
                response = body(state, context.CancellationToken);
            }
            catch (Exception exception)
            {
                response = DurableTaskResponse.FromException(exception);
            }

            context.OnBodyCompleted(response);
        }

        return new(response);
    }

    /// <summary>Runs an asynchronous delegate and creates its response from its completed task.</summary>
    /// <remarks>
    /// The completion is reported from a continuation which runs synchronously with the delegate's final code, on the
    /// scheduler which started the delegate, so a host which runs the delegate's code on that scheduler stages the
    /// response in the same work item as the code's state changes.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    public static ValueTask<DurableTaskResponse> RunAsync<TTask>(
        DurableTaskContext context,
        Func<CancellationToken, TTask> function,
        Func<TTask, DurableTaskResponse> getResponse)
        where TTask : Task
    {
        TTask task;
        using (DurableTaskContext.Enter(context))
        {
            try
            {
                task = function(context.CancellationToken)
                    ?? throw new InvalidOperationException("The durable task delegate returned a null task.");
            }
            catch (Exception exception)
            {
                var failure = DurableTaskResponse.FromException(exception);
                context.OnBodyCompleted(failure);
                return new(failure);
            }

            if (task.IsCompleted)
            {
                return new(Complete(context, task, getResponse));
            }
        }

        return new(task.ContinueWith(
            static (completed, state) =>
            {
                var (context, getResponse) = ((DurableTaskContext, Func<TTask, DurableTaskResponse>))state!;
                using (DurableTaskContext.Enter(context))
                {
                    return Complete(context, (TTask)completed, getResponse);
                }
            },
            (context, getResponse),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Current));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    private static DurableTaskResponse Complete<TTask>(
        DurableTaskContext context,
        TTask task,
        Func<TTask, DurableTaskResponse> getResponse)
        where TTask : Task
    {
        DurableTaskResponse response;
        try
        {
            response = getResponse(task);
        }
        catch (Exception exception)
        {
            response = DurableTaskResponse.FromException(exception);
        }

        context.OnBodyCompleted(response);
        return response;
    }
}

internal sealed class DelegateDurableTask(Action<CancellationToken> action) : DurableTask
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        => DelegateBody.Run(
            context,
            static (action, token) =>
            {
                action(token);
                return DurableTaskResponse.Completed;
            },
            action);
}

internal sealed class DelegateDurableTask<TResult>(Func<CancellationToken, TResult> function) : DurableTask<TResult>
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        => DelegateBody.Run(context, static (function, token) => DurableTaskResponse.FromResult(function(token)), function);
}

internal sealed class AsyncDelegateDurableTask(Func<CancellationToken, Task> function) : DurableTask
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        => DelegateBody.RunAsync(
            context,
            function,
            static task =>
            {
                task.GetAwaiter().GetResult();
                return DurableTaskResponse.Completed;
            });
}

internal sealed class AsyncDelegateDurableTask<TResult>(Func<CancellationToken, Task<TResult>> function) : DurableTask<TResult>
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
        => DelegateBody.RunAsync(context, function, static task => DurableTaskResponse.FromResult(task.GetAwaiter().GetResult()));
}

/// <summary>
/// An immutable durable task configuration. A configuration without an identifier is created only to await its task,
/// and that run generates the child identifier; handle operations are reachable only from configurations which carry an
/// identifier.
/// </summary>
internal readonly struct ConfiguredDurableTaskCore<TTask> where TTask : DurableTask
{
    private readonly TaskId _taskId;

    internal ConfiguredDurableTaskCore(TTask task) : this(task, DurableTaskContext.Ambient)
    {
    }

    internal ConfiguredDurableTaskCore(TTask task, DurableTaskContext? parentContext)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        ParentContext = parentContext;
    }

    private ConfiguredDurableTaskCore(TTask task, DurableTaskContext? parentContext, TaskId taskId)
    {
        Task = task;
        ParentContext = parentContext;
        _taskId = taskId;
    }

    internal TTask Task { get; }

    internal DurableTaskContext? ParentContext { get; }

    internal TaskId TaskId => _taskId;

    internal ConfiguredDurableTaskCore<TTask> WithId(string segment)
    {
        ThrowIfIdSpecified();
        return new(Task, ParentContext, ParentContext is { } parent ? parent.CreateChildTaskId(segment) : TaskId.CreateRoot(segment));
    }

    internal ConfiguredDurableTaskCore<TTask> WithId(TaskId taskId)
    {
        ThrowIfIdSpecified();
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable task identifier cannot be empty.", nameof(taskId));
        }

        return new(Task, ParentContext, taskId);
    }

    private void ThrowIfIdSpecified()
    {
        if (!_taskId.IsDefault)
        {
            throw new InvalidOperationException("The durable task identifier has already been specified.");
        }
    }

    private TaskId GetOrCreateId()
    {
        if (!_taskId.IsDefault)
        {
            return _taskId;
        }

        return ParentContext is { } parent
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

        var taskId = GetOrCreateId();
        if (ParentContext is { } parent)
        {
            return (null, await parent.ScheduleChildTaskAsync(taskId, Task, cancellationToken).ConfigureAwait(false));
        }

        if (Task is not ISchedulableTask schedulable)
        {
            throw new InvalidOperationException("The durable task definition does not support root scheduling.");
        }

        var response = await schedulable.ScheduleAsync(taskId, cancellationToken).ConfigureAwait(false);
        return response.IsCompleted
            ? (response, null)
            : (null, schedulable.GetHandle(taskId));
    }

    internal IScheduledTaskHandle GetHandle()
    {
        if (_taskId.IsDefault)
        {
            throw new InvalidOperationException("The durable task identifier has not been specified.");
        }

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
/// <remarks><see cref="WithId(string)"/> returns a new configuration and leaves this one unchanged.</remarks>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "This fluent configuration value has no equality contract.")]
public readonly struct ConfiguredDurableTask
{
    private readonly ConfiguredDurableTaskCore<DurableTask> _core;

    internal ConfiguredDurableTask(DurableTask task) => _core = new(task);

    internal ConfiguredDurableTask(DurableTask task, string rootId)
        => _core = new ConfiguredDurableTaskCore<DurableTask>(task, parentContext: null).WithId(TaskId.CreateRoot(rootId));

    internal ConfiguredDurableTask(DurableTask task, TaskId taskId) => _core = new ConfiguredDurableTaskCore<DurableTask>(task).WithId(taskId);

    private ConfiguredDurableTask(ConfiguredDurableTaskCore<DurableTask> core) => _core = core;

    /// <summary>Gets an awaiter which runs or attaches to the task.</summary>
    public DurableTaskAwaiter GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    /// <summary>
    /// Returns a configuration which assigns a stable logical identifier segment. Within a durable execution, a segment
    /// repeated in one run identifies its later occurrences by their order, such as <c>reserve$2</c>, and segments don't
    /// contain <c>$</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This configuration already has an identifier.</exception>
    public ConfiguredDurableTask WithId(string segment) => new(_core.WithId(segment));

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
    /// The reason which the task's <see cref="DurableTaskContext.CancellationReason"/> reports, or
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
/// <remarks><see cref="WithId(string)"/> returns a new configuration and leaves this one unchanged.</remarks>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "This fluent configuration value has no equality contract.")]
public readonly struct ConfiguredDurableTask<TResult>
{
    private readonly ConfiguredDurableTaskCore<DurableTask<TResult>> _core;

    internal ConfiguredDurableTask(DurableTask<TResult> task) => _core = new(task);

    internal ConfiguredDurableTask(DurableTask<TResult> task, string rootId)
        => _core = new ConfiguredDurableTaskCore<DurableTask<TResult>>(task, parentContext: null).WithId(TaskId.CreateRoot(rootId));

    internal ConfiguredDurableTask(DurableTask<TResult> task, TaskId taskId)
        => _core = new ConfiguredDurableTaskCore<DurableTask<TResult>>(task).WithId(taskId);

    private ConfiguredDurableTask(ConfiguredDurableTaskCore<DurableTask<TResult>> core) => _core = core;

    /// <summary>Gets an awaiter which runs or attaches to the task.</summary>
    public DurableTaskAwaiter<TResult> GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    /// <summary>
    /// Returns a configuration which assigns a stable logical identifier segment. Within a durable execution, a segment
    /// repeated in one run identifies its later occurrences by their order, such as <c>reserve$2</c>, and segments don't
    /// contain <c>$</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This configuration already has an identifier.</exception>
    public ConfiguredDurableTask<TResult> WithId(string segment) => new(_core.WithId(segment));

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
    /// The reason which the task's <see cref="DurableTaskContext.CancellationReason"/> reports, or
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

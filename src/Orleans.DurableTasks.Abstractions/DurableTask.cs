using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    /// <summary>Creates an unstarted step which runs <paramref name="action"/> as a child execution when it is awaited, scheduled, or posted.</summary>
    public static DurableTask Run(Action<CancellationToken> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new DelegateDurableTask(action);
    }

    /// <summary>Creates an unstarted step which runs <paramref name="function"/> as a child execution when it is awaited, scheduled, or posted.</summary>
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new DelegateDurableTask<TResult>(function);
    }

    /// <summary>Creates an unstarted step which runs asynchronous <paramref name="function"/> as a child execution when it is awaited, scheduled, or posted.</summary>
    /// <remarks>An asynchronous lambda also converts to other delegates which <c>Run</c> accepts, and this overload takes precedence.</remarks>
    [OverloadResolutionPriority(1)]
    public static DurableTask Run(Func<CancellationToken, Task> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask(function);
    }

    /// <summary>Creates an unstarted step which runs asynchronous <paramref name="function"/> as a child execution when it is awaited, scheduled, or posted.</summary>
    /// <remarks>An asynchronous lambda also converts to other delegates which <c>Run</c> accepts, and this overload takes precedence.</remarks>
    [OverloadResolutionPriority(1)]
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, Task<TResult>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask<TResult>(function);
    }

    /// <summary>
    /// Creates an unstarted step. It runs <paramref name="function"/> when it is awaited, scheduled, or posted, possibly in
    /// a later turn, as a child execution with its own <see cref="Current"/> and cancellation scope. Unlike
    /// <see cref="Task.Run(Func{Task})"/>, creating it starts nothing, and nothing runs on the thread pool.
    /// </summary>
    /// <typeparam name="TResult">The type of the function's result, which the host stores with the child.</typeparam>
    /// <param name="function">The asynchronous function which the child runs.</param>
    /// <returns>A definition which runs the function each time it is awaited, scheduled, or posted.</returns>
    /// <remarks>
    /// <para>
    /// The child records the result of the function's task, or the exception which it threw, and a replay returns the
    /// recorded outcome without running the function. <see cref="RunInline{TResult}(string, Func{TResult})"/> runs a
    /// synchronous effect in the caller's work item instead. Because the step runs later, as a child, a step which
    /// isn't shielded completes as canceled when the parent's cancellation was requested before it starts, and its
    /// record commits in a journal write of its own. A step and an effect with the same name share an identifier, and a
    /// replay which finds the other kind recorded under it is a divergence.
    /// </para>
    /// <para>
    /// An asynchronous lambda also converts to other delegates which <c>Run</c> accepts, and this overload takes
    /// precedence.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static DurableTask<TResult> Run<TResult>(Func<Task<TResult>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask<TResult>(_ => function());
    }

    /// <summary>Creates an unstarted step which runs asynchronous <paramref name="function"/> as a child execution when it is awaited, scheduled, or posted.</summary>
    /// <param name="function">The asynchronous function which the child runs.</param>
    /// <returns>A definition which runs the function each time it is awaited, scheduled, or posted.</returns>
    /// <remarks>The child runs and records its outcome as <see cref="Run{TResult}(Func{Task{TResult}})"/> describes.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    [OverloadResolutionPriority(1)]
    public static DurableTask Run(Func<Task> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask(_ => function());
    }

    /// <summary>Creates an unstarted step which runs asynchronous <paramref name="function"/> as a child execution when it is awaited, scheduled, or posted.</summary>
    /// <typeparam name="TResult">The type of the function's result, which the host stores with the child.</typeparam>
    /// <param name="function">The asynchronous function which the child runs.</param>
    /// <returns>A definition which runs the function each time it is awaited, scheduled, or posted.</returns>
    /// <remarks>The child runs and records its outcome as <see cref="Run{TResult}(Func{Task{TResult}})"/> describes.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    public static DurableTask<TResult> Run<TResult>(Func<ValueTask<TResult>> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask<TResult>(_ => function().AsTask());
    }

    /// <summary>Creates an unstarted step which runs asynchronous <paramref name="function"/> as a child execution when it is awaited, scheduled, or posted.</summary>
    /// <param name="function">The asynchronous function which the child runs.</param>
    /// <returns>A definition which runs the function each time it is awaited, scheduled, or posted.</returns>
    /// <remarks>The child runs and records its outcome as <see cref="Run{TResult}(Func{Task{TResult}})"/> describes.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    public static DurableTask Run(Func<ValueTask> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return new AsyncDelegateDurableTask(_ => function().AsTask());
    }

    private const string RunAwaitsDurableTasks =
        "DurableTask.Run runs a delegate as a durable child, and a durable task already runs as one. Await the durable task directly.";

    /// <summary>Rejects a delegate which returns a durable task, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static DurableTask<TResult> Run<TResult>(Func<DurableTask<TResult>> function) => throw new NotSupportedException(RunAwaitsDurableTasks);

    /// <summary>Rejects a delegate which returns a durable task, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static DurableTask Run(Func<DurableTask> function) => throw new NotSupportedException(RunAwaitsDurableTasks);

    /// <summary>Rejects a delegate which returns a durable task, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static DurableTask<TResult> Run<TResult>(Func<CancellationToken, DurableTask<TResult>> function) => throw new NotSupportedException(RunAwaitsDurableTasks);

    /// <summary>Rejects a delegate which returns a durable task, which runs as a durable child when it is awaited.</summary>
    [Obsolete(RunAwaitsDurableTasks, error: true)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static DurableTask Run(Func<CancellationToken, DurableTask> function) => throw new NotSupportedException(RunAwaitsDurableTasks);

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
    {
        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(context.ScheduleDelayAsync(context.Id, context.UtcNow + duration, context.CancellationToken));
    }
}

internal sealed class DelayUntilDurableTask(DateTimeOffset dueTime) : DurableTask
{
    protected internal override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context)
    {
        context.ThrowIfAttemptCanceled();
        return context.ObserveAttempt(context.ScheduleDelayAsync(context.Id, dueTime, context.CancellationToken));
    }
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
/// An immutable durable task configuration: an identifier, and whether the task is detached or shielded. Handle
/// operations are reachable only from configurations which carry an identifier.
/// </summary>
/// <remarks>
/// Within a durable execution, <see cref="WithId(string)"/> assigns an attached child's identifier when it's called, so
/// handle operations on copies of the configuration address the same child. A detached task uses the name as its root
/// identifier verbatim. Outside a durable execution, every task is a root, and the name is its root identifier.
/// </remarks>
internal readonly struct ConfiguredDurableTaskCore<TTask> where TTask : DurableTask
{
    private readonly TaskId _taskId;
    private readonly string? _name;
    private readonly bool _detached;
    private readonly bool _shielded;

    internal ConfiguredDurableTaskCore(TTask task) : this(task, DurableTaskContext.Ambient)
    {
    }

    internal ConfiguredDurableTaskCore(TTask task, DurableTaskContext? parentContext)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        ParentContext = parentContext;
    }

    private ConfiguredDurableTaskCore(
        TTask task,
        DurableTaskContext? parentContext,
        TaskId taskId,
        string? name,
        bool detached,
        bool shielded)
    {
        Task = task;
        ParentContext = parentContext;
        _taskId = taskId;
        _name = name;
        _detached = detached;
        _shielded = shielded;
    }

    internal TTask Task { get; }

    internal DurableTaskContext? ParentContext { get; }

    private bool IsDetachedFromParent => _detached && ParentContext is not null;

    internal ConfiguredDurableTaskCore<TTask> WithId(string name)
    {
        ThrowIfIdSpecified();
        var taskId = ParentContext is { } parent && !_detached ? parent.CreateChildTaskId(name) : TaskId.CreateRoot(name);
        return new(Task, ParentContext, taskId, name, _detached, _shielded);
    }

    internal ConfiguredDurableTaskCore<TTask> WithId(TaskId taskId)
    {
        ThrowIfIdSpecified();
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable task identifier cannot be empty.", nameof(taskId));
        }

        return new(Task, ParentContext, taskId, name: null, _detached, _shielded);
    }

    internal ConfiguredDurableTaskCore<TTask> Detached()
    {
        if (_detached || ParentContext is null)
        {
            // Outside a durable execution, every task is already a root.
            return this;
        }

        Debug.Assert(_taskId.IsDefault || _name is not null, "Only named configurations carry a public identifier.");
        var taskId = _name is { } name ? TaskId.CreateRoot(name) : default;
        return new(Task, ParentContext, taskId, _name, detached: true, _shielded);
    }

    internal ConfiguredDurableTaskCore<TTask> Shielded() => new(Task, ParentContext, _taskId, _name, _detached, shielded: true);

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

        return ParentContext switch
        {
            null => TaskId.CreateRoot(Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)),
            { } parent when _detached => parent.CreateDetachedRootId(),
            { } parent => parent.CreateChildTaskId(name: null),
        };
    }

    internal async Task<DurableTaskResponse> RunAsync(CancellationToken cancellationToken)
    {
        ParentContext?.ThrowIfAttemptCanceled();
        if (ParentContext is { } parent && !_detached)
        {
            // The shield follows this method's asynchronous flow, and ends when the method returns to its caller.
            if (_shielded)
            {
                parent.EnterShield();
            }

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

        var started = await StartAsync(DurableTaskStartOptions.None, cancellationToken).ConfigureAwait(false);
        return started.Response ?? await HostHandleOperations.WaitAsync(started.Handle!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the task with <paramref name="options"/>, adding <see cref="DurableTaskStartOptions.Detached"/> for a
    /// detached configuration, and returns its identifier and either its terminal response or its handle.
    /// </summary>
    internal async Task<(TaskId TaskId, DurableTaskResponse? Response, IScheduledTaskHandle? Handle)> StartAsync(
        DurableTaskStartOptions options,
        CancellationToken cancellationToken)
    {
        ThrowIfInlineOnly();
        var taskId = GetOrCreateId();
        if (ParentContext is { } parent)
        {
            parent.ThrowIfAttemptCanceled();
            if (_shielded)
            {
                parent.EnterShield();
            }

            var handle = await parent.ObserveAttempt(parent.StartTaskAsync(taskId, Task, options | GetDetachedOption(), cancellationToken))
                .ConfigureAwait(false);
            return (handle.TaskId, null, handle);
        }

        var schedulable = GetSchedulableTask();
        var response = await schedulable.ScheduleAsync(taskId, cancellationToken).ConfigureAwait(false);
        return response.IsCompleted
            ? (taskId, response, null)
            : (taskId, null, schedulable.GetHandle(taskId));
    }

    internal IScheduledTaskHandle Post()
    {
        ThrowIfInlineOnly();
        var taskId = GetOrCreateId();
        if (ParentContext is not { } parent)
        {
            return GetSchedulableTask().Post(taskId);
        }

        parent.ThrowIfAttemptCanceled();
        var previousShield = _shielded ? parent.EnterShield() : null;
        try
        {
            return parent.PostTask(taskId, Task, GetDetachedOption());
        }
        finally
        {
            if (_shielded)
            {
                DurableTaskContext.RestoreShield(previousShield);
            }
        }
    }

    internal IScheduledTaskHandle GetHandle()
    {
        if (_taskId.IsDefault)
        {
            throw new InvalidOperationException("The durable task identifier has not been specified.");
        }

        if (IsDetachedFromParent)
        {
            throw new InvalidOperationException(
                "Within a durable execution, a detached task is observed through the handle which ScheduleAsync or Post returns.");
        }

        if (ParentContext is { } parent)
        {
            parent.ThrowIfAttemptCanceled();
            return parent.GetChildTaskHandle(_taskId);
        }

        return GetSchedulableTask().GetHandle(_taskId);
    }

    private DurableTaskStartOptions GetDetachedOption() => _detached ? DurableTaskStartOptions.Detached : DurableTaskStartOptions.None;

    private void ThrowIfInlineOnly()
    {
        if (Task is IInlineOperation { InlineOnly: true })
        {
            throw IInlineOperation.CreateInlineOnlyException();
        }
    }

    private ISchedulableTask GetSchedulableTask() =>
        Task as ISchedulableTask
        ?? throw new InvalidOperationException("The durable task definition does not support root scheduling.");
}

/// <summary>Configures and starts a durable task.</summary>
/// <remarks>
/// <para>
/// Each configuration method returns a new configuration and leaves this one unchanged. <see cref="WithId(string)"/>
/// names the task, <see cref="Detached"/> makes it an independent root task, and <see cref="Shielded"/> keeps the
/// starting execution's durable cancellation from reaching it.
/// </para>
/// <para>
/// Awaiting the configuration runs the task and returns once its result is durable. <see cref="ScheduleAsync"/> returns
/// a handle once the task's host has accepted and durably stored the task, and <see cref="Post"/> stages the start with
/// the caller's next durable write and returns its handle at once.
/// </para>
/// </remarks>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "This fluent configuration value has no equality contract.")]
public readonly struct ConfiguredDurableTask
{
    private readonly ConfiguredDurableTaskCore<DurableTask> _core;

    internal ConfiguredDurableTask(DurableTask task) => _core = new(task);

    internal ConfiguredDurableTask(DurableTask task, TaskId taskId) => _core = new ConfiguredDurableTaskCore<DurableTask>(task).WithId(taskId);

    private ConfiguredDurableTask(ConfiguredDurableTaskCore<DurableTask> core) => _core = core;

    /// <summary>Gets an awaiter which runs or attaches to the task and returns once its result is durable.</summary>
    public DurableTaskAwaiter GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    /// <inheritdoc cref="DurableTaskExtensions.WithId(DurableTask, string)"/>
    /// <exception cref="InvalidOperationException">This configuration already has an identifier.</exception>
    public ConfiguredDurableTask WithId(string name) => new(_core.WithId(name));

    /// <inheritdoc cref="DurableTaskExtensions.Detached(DurableTask)"/>
    public ConfiguredDurableTask Detached() => new(_core.Detached());

    /// <inheritdoc cref="DurableTaskExtensions.Shielded(DurableTask)"/>
    public ConfiguredDurableTask Shielded() => new(_core.Shielded());

    /// <summary>
    /// Starts the task and returns its handle once the task's host has accepted and durably stored the task.
    /// </summary>
    /// <param name="cancellationToken">A token which abandons the start. It doesn't cancel the task.</param>
    /// <returns>The task's handle, whose <see cref="ScheduledTask.Id"/> addresses the task.</returns>
    /// <remarks>
    /// The task's host holds the task when this returns, so its identifier can be handed to other parties, which address
    /// it, for example with <c>GetDurableTask</c>. Within a durable execution, the start is recorded in the execution, and a
    /// replay returns the same handle.
    /// </remarks>
    public async Task<ScheduledTask> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        var started = await _core.StartAsync(DurableTaskStartOptions.WaitForAcceptance, cancellationToken).ConfigureAwait(false);
        return started.Response is { } response
            ? new CompletedScheduledTask(started.TaskId, response)
            : new ScheduledTaskHandle(started.Handle!);
    }

    /// <summary>
    /// Stages the start of the task in the caller's durable outbox, and returns its handle without waiting.
    /// </summary>
    /// <returns>The task's handle, which the caller can await later.</returns>
    /// <remarks>
    /// The start commits with the caller's next durable write, in the same write as the state changes which the calling
    /// code made, and is then delivered exactly once. Within a durable execution, the start is recorded like
    /// <see cref="DurableTask.RunInline{TResult}(Func{TResult})"/>, so a replay returns the same handle without starting the
    /// task again. In an ordinary method of a grain which has a durable outbox, the task is a root task.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The caller has no durable outbox, for example because it's a client.</exception>
    public ScheduledTask Post() => new ScheduledTaskHandle(_core.Post());

    /// <summary>Requests durable cancellation.</summary>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
        => HostHandleOperations.CancelAsync(_core.GetHandle(), cancellationToken);

    /// <summary>Requests durable cancellation carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or
    /// <see langword="null"/> for none. The first request determines the reason.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken = default)
        => HostHandleOperations.CancelAsync(_core.GetHandle(), reason, cancellationToken);

    /// <summary>Polls the task and returns its current status.</summary>
    public async Task<DurableTaskStatus> PollAsync(
        PollingOptions options = default,
        CancellationToken cancellationToken = default)
        => (await HostHandleOperations.PollAsync(_core.GetHandle(), options, cancellationToken).ConfigureAwait(false)).Status;
}

/// <summary>Configures and starts a durable task with a result.</summary>
/// <remarks><inheritdoc cref="ConfiguredDurableTask" path="/remarks"/></remarks>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "This fluent configuration value has no equality contract.")]
public readonly struct ConfiguredDurableTask<TResult>
{
    private readonly ConfiguredDurableTaskCore<DurableTask<TResult>> _core;

    internal ConfiguredDurableTask(DurableTask<TResult> task) => _core = new(task);

    internal ConfiguredDurableTask(DurableTask<TResult> task, TaskId taskId)
        => _core = new ConfiguredDurableTaskCore<DurableTask<TResult>>(task).WithId(taskId);

    private ConfiguredDurableTask(ConfiguredDurableTaskCore<DurableTask<TResult>> core) => _core = core;

    /// <summary>Gets an awaiter which runs or attaches to the task and returns its result once the result is durable.</summary>
    public DurableTaskAwaiter<TResult> GetAwaiter() => new(_core.RunAsync(CancellationToken.None));

    /// <inheritdoc cref="DurableTaskExtensions.WithId(DurableTask, string)"/>
    /// <exception cref="InvalidOperationException">This configuration already has an identifier.</exception>
    public ConfiguredDurableTask<TResult> WithId(string name) => new(_core.WithId(name));

    /// <inheritdoc cref="DurableTaskExtensions.Detached(DurableTask)"/>
    public ConfiguredDurableTask<TResult> Detached() => new(_core.Detached());

    /// <inheritdoc cref="DurableTaskExtensions.Shielded(DurableTask)"/>
    public ConfiguredDurableTask<TResult> Shielded() => new(_core.Shielded());

    /// <inheritdoc cref="ConfiguredDurableTask.ScheduleAsync(CancellationToken)"/>
    public async Task<ScheduledTask<TResult>> ScheduleAsync(CancellationToken cancellationToken = default)
    {
        var started = await _core.StartAsync(DurableTaskStartOptions.WaitForAcceptance, cancellationToken).ConfigureAwait(false);
        return started.Response is { } response
            ? new CompletedScheduledTask<TResult>(started.TaskId, response)
            : new ScheduledTaskHandle<TResult>(started.Handle!);
    }

    /// <inheritdoc cref="ConfiguredDurableTask.Post"/>
    public ScheduledTask<TResult> Post() => new ScheduledTaskHandle<TResult>(_core.Post());

    /// <summary>Requests durable cancellation.</summary>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default)
        => HostHandleOperations.CancelAsync(_core.GetHandle(), cancellationToken);

    /// <summary>Requests durable cancellation carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or
    /// <see langword="null"/> for none. The first request determines the reason.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken = default)
        => HostHandleOperations.CancelAsync(_core.GetHandle(), reason, cancellationToken);

    /// <summary>Polls the task and returns its current status.</summary>
    public async Task<DurableTaskStatus> PollAsync(
        PollingOptions options = default,
        CancellationToken cancellationToken = default)
        => (await HostHandleOperations.PollAsync(_core.GetHandle(), options, cancellationToken).ConfigureAwait(false)).Status;
}

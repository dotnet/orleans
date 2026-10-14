using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Orleans.DurableTasks;

/// <summary>Supplies host services and deterministic execution state to a durable task.</summary>
public abstract class DurableExecutionContext
{
    private const char GeneratedSegmentPrefix = '$';
    private static readonly AsyncLocal<DurableExecutionContext?> AmbientContext = new();
    private static readonly AsyncLocal<DurableExecutionContext?> CancellationSuppressedContext = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancellationSource = new();
    private readonly CancellationTokenSource? _executionCancellationSource;
    private readonly CancellationToken _executionAbortToken;
    private List<CancellationRegistration>? _registrations;
    private CancellationOperation? _cancellationOperation;
    private bool _cancellationRequested;
    private string? _cancellationReason;
    private long _nextChildId;
    private long _nextOperationId;

    /// <summary>Initializes an execution with durable cancellation.</summary>
    protected DurableExecutionContext(TaskId taskId) : this(taskId, CancellationToken.None)
    {
    }

    /// <summary>Initializes an execution with durable cancellation and a host execution-abort token.</summary>
    /// <param name="taskId">The stable identifier of this execution.</param>
    /// <param name="executionAbortToken">The host-owned token for aborting this execution attempt.</param>
    /// <remarks>
    /// <see cref="CancellationToken"/>, and the delegate and delay operations which receive it, observe both durable
    /// cancellation and host execution abort. <see cref="IsCancellationRequested"/> and durable cancellation callbacks
    /// observe the durable request only. The host owns the abort source for this execution's lifetime, drains
    /// the aborted attempt, discards its response, and replays it in a new context.
    /// </remarks>
    protected DurableExecutionContext(TaskId taskId, CancellationToken executionAbortToken)
    {
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable execution requires an explicit task identifier.", nameof(taskId));
        }

        TaskId = taskId;
        _executionAbortToken = executionAbortToken;
        if (executionAbortToken.CanBeCanceled)
        {
            _executionCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellationSource.Token,
                executionAbortToken);
        }
    }

    /// <summary>Gets the current durable execution context.</summary>
    public static DurableExecutionContext? Current => AmbientContext.Value;

    /// <summary>Gets this execution's identifier.</summary>
    public TaskId TaskId { get; }

    /// <summary>Gets the host-provided logical UTC time for this execution.</summary>
    public abstract DateTimeOffset UtcNow { get; }

    /// <summary>Gets a value indicating whether durable cancellation has been requested.</summary>
    public bool IsCancellationRequested
    {
        get
        {
            lock (_lock)
            {
                return _cancellationRequested;
            }
        }
    }

    /// <summary>
    /// Gets the reason which the durable cancellation request carries, or <see langword="null"/> when cancellation hasn't
    /// been requested or its request carries no reason.
    /// </summary>
    /// <remarks>
    /// The first cancellation request determines the reason, and later requests leave it unchanged. The host retains the
    /// reason with the task's cancellation, so a replay observes the same reason. The reason is set before the durable
    /// token is canceled, so code which observes the cancellation, such as a <see langword="catch"/> block, reads it.
    /// </remarks>
    public string? CancellationReason
    {
        get
        {
            lock (_lock)
            {
                return _cancellationReason;
            }
        }
    }

    /// <summary>
    /// Starts a scope in which the current durable execution schedules children which its durable cancellation doesn't
    /// reach, such as compensation which a canceled workflow runs inline.
    /// </summary>
    /// <returns>The scope, which ends when it is disposed.</returns>
    /// <remarks>
    /// <para>
    /// Children scheduled in the scope, including local steps, delays, calls to other grains, and combinator inputs, run
    /// to completion: a cancellation which the execution observed before scheduling them doesn't cancel them, and a later
    /// one doesn't reach them. The host records this with each child, so it holds across replay. The execution's own
    /// <see cref="CancellationToken"/>, <see cref="IsCancellationRequested"/>, and <see cref="CancellationReason"/>
    /// still report its cancellation, and operations which run as part of the execution itself, such as a signal wait,
    /// still observe it. An administrative abort still reaches the children, and so does deleting the execution's tree.
    /// </para>
    /// <para>
    /// The scope follows the asynchronous flow which entered it, across awaits, and applies only to the execution which
    /// entered it: a child's own children are reached by the child's cancellation as usual. Scopes nest, and disposing a
    /// scope restores the state which preceded it. Work which must outlive the execution itself runs as a detached task.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The caller isn't running in a durable execution.</exception>
    public static CancellationSuppressionScope SuppressCancellation()
    {
        var context = Current
            ?? throw new InvalidOperationException("Durable cancellation can be suppressed only within a durable execution.");
        var previous = CancellationSuppressedContext.Value;
        CancellationSuppressedContext.Value = context;
        return new(previous);
    }

    /// <summary>
    /// Gets whether the calling code runs in a <see cref="SuppressCancellation"/> scope of this execution, so that a child
    /// which it schedules isn't reached by this execution's durable cancellation.
    /// </summary>
    /// <remarks>
    /// Hosts read this when they schedule a child, before any asynchronous work, and record it with the child: a host
    /// creates and runs such a child even after this execution's cancellation was requested, and doesn't propagate this
    /// execution's cancellation to it.
    /// </remarks>
    protected internal bool IsCancellationSuppressed => ReferenceEquals(CancellationSuppressedContext.Value, this);

    internal static void RestoreCancellationSuppression(DurableExecutionContext? previous) =>
        CancellationSuppressedContext.Value = previous;

    /// <summary>
    /// Gets the token which is canceled when durable cancellation is requested or the host aborts this execution attempt.
    /// </summary>
    /// <remarks>
    /// <see cref="IsCancellationRequested"/> reports durable cancellation only, so code which decides a durable outcome,
    /// such as whether to compensate, checks it rather than this token. An aborted attempt's response is discarded and the
    /// task replays in a new context.
    ///
    /// <para>
    /// Registrations on this token are ordinary synchronous .NET cleanup observers only. They follow
    /// standard <see cref="CancellationToken"/> behavior, including registration-time
    /// <see cref="ExecutionContext"/> capture, optional <see cref="SynchronizationContext"/> dispatch,
    /// and current execution-context behavior for unsafe registrations.
    /// </para>
    ///
    /// <para>
    /// These observers must return promptly. They must not call, block on, await, or otherwise
    /// orchestrate <see cref="DurableTaskRuntimeHelper.RequestCancellationAsync(DurableExecutionContext, CancellationToken)"/> or durable
    /// cancellation of any context. Doing so is outside this contract and has the usual synchronous
    /// reentrancy and sync-over-async risks. Use <see cref="RegisterCancellationCallbackAsync"/> for
    /// all cancellation work which requests another durable context, awaits asynchronous work, needs
    /// cycle handling, or participates in durable failure aggregation.
    /// </para>
    /// </remarks>
    public CancellationToken CancellationToken => _executionCancellationSource?.Token ?? _cancellationSource.Token;

    /// <summary>Gets the host-owned token for aborting this execution attempt.</summary>
    internal CancellationToken ExecutionAbortToken => _executionAbortToken;

    /// <summary>Schedules or reattaches to a child definition under an exact identifier.</summary>
    protected internal abstract ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(
        TaskId taskId,
        DurableTask taskDefinition,
        CancellationToken cancellationToken);

    /// <summary>Schedules a durable delay at <paramref name="dueTime"/>.</summary>
    protected internal abstract ValueTask<DurableTaskResponse> ScheduleDelayAsync(
        TaskId taskId,
        DateTimeOffset dueTime,
        CancellationToken cancellationToken);

    /// <summary>Gets the host handle for an existing child identifier.</summary>
    protected internal abstract IScheduledTaskHandle GetChildTaskHandle(TaskId taskId);

    /// <summary>
    /// Starts <paramref name="taskDefinition"/> as a detached root task under <paramref name="rootId"/> and records the
    /// start under <paramref name="markerId"/>, a descendant of this execution.
    /// </summary>
    /// <remarks>
    /// The detached task isn't part of this execution: durable cancellation of this execution doesn't cancel it, and this
    /// execution doesn't observe its result. The host commits the start together with its record, so a replay which
    /// reaches the same marker leaves the detached task unchanged. A marker reused for another detached start fails.
    /// The returned operation completes once the start is durable. Hosts which don't support detached tasks keep this
    /// default implementation, which throws <see cref="NotSupportedException"/>.
    /// </remarks>
    protected internal virtual ValueTask DetachTaskAsync(
        TaskId markerId,
        TaskId rootId,
        DurableTask taskDefinition,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("The durable execution host does not support detached tasks.");

    /// <summary>
    /// Waits for the signal named <paramref name="name"/> of the invoked task whose execution contains this execution,
    /// and returns a successful response holding the signal's recorded value.
    /// </summary>
    /// <remarks>
    /// The host records the first value it receives for each name of an invoked task, together with that task's state,
    /// and returns it to every wait for that name, including waits during replay. A signal which arrived before the wait
    /// completes it immediately. Canceling <paramref name="cancellationToken"/> abandons the wait. Hosts which don't
    /// support signals keep this default implementation, which throws <see cref="NotSupportedException"/>.
    /// </remarks>
    protected internal virtual ValueTask<DurableTaskResponse> WaitForSignalAsync(
        string name,
        CancellationToken cancellationToken)
        => throw new NotSupportedException("The durable execution host does not support signals.");

    /// <summary>
    /// Returns a completed candidate, and returns the same candidate for every call with the same
    /// <paramref name="decisionId"/>, including calls during replay.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The candidate is the one which completed first: a host persists the selection under <paramref name="decisionId"/>,
    /// or derives it from a durable completion order, such as a sequence which stamps each terminal result when the host
    /// stages it. A durable order needs no record: the completed candidate with the lowest sequence is the same in every
    /// replay, because candidates which complete later receive higher sequences.
    /// </para>
    /// <para>
    /// <see cref="DurableTask.WhenAny(IReadOnlyList{DurableTask})"/> selects its winner with one call, and
    /// <see cref="ScheduledTask.WhenEach(IReadOnlyList{ScheduledTask})"/> selects each next task with one call over the
    /// remaining candidates, under a distinct decision identifier. The host reads <paramref name="candidates"/> only during
    /// the call.
    /// </para>
    /// </remarks>
    protected internal abstract ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a child identifier, using <paramref name="name"/> when supplied.
    /// Explicit names beginning with <c>$</c> are reserved for generated identifiers.
    /// </summary>
    /// <remarks>
    /// Generated identifiers are replay-stable when unnamed children are created in the same
    /// deterministic order. A concurrent batch can use
    /// <see cref="DurableTask.WhenAll(IReadOnlyList{DurableTask})"/> and
    /// <see cref="DurableTask.WhenAny(IReadOnlyList{DurableTask})"/>, whose input indexes provide
    /// stable logical slots. Concurrent independent scheduling operations, including concurrent
    /// combinator invocations, must use explicit names or otherwise begin in deterministic order.
    /// </remarks>
    protected internal virtual TaskId CreateChildTaskId(string? name)
    {
        if (name is not null)
        {
            if (name.StartsWith(GeneratedSegmentPrefix))
            {
                throw new ArgumentException(
                    $"Explicit child names beginning with '{GeneratedSegmentPrefix}' are reserved for generated identifiers.",
                    nameof(name));
            }

            return TaskId.Child(name);
        }

        return TaskId.Child(
            $"$child-{Interlocked.Increment(ref _nextChildId).ToString(CultureInfo.InvariantCulture)}");
    }

    internal TaskId CreateOperationId(string kind)
        => TaskId.Child($"${kind}-{Interlocked.Increment(ref _nextOperationId).ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Registers an asynchronous callback which observes the durable cancellation request.
    /// </summary>
    /// <remarks>
    /// This API establishes cancellation-operation causality. The callback executes with this durable
    /// context as <see cref="Current"/> and participates in durable cancellation dependency tracking
    /// and failure aggregation. Its cancellation operation flows with <see cref="ExecutionContext"/>
    /// across ordinary awaits and safe thread-pool dispatch, including <see cref="Task.Run(Action)"/>.
    /// Suppressing execution-context flow or using unsafe dispatch detaches subsequent asynchronous
    /// work according to standard .NET behavior, so cancellation requests made by that work are
    /// external rather than durable dependencies. Dependency edges are based only on this explicitly
    /// flowed operation; no thread or global activity is inferred.
    ///
    /// <para>
    /// Disposing the returned registration prevents an invocation which has not started, or
    /// asynchronously waits for an active invocation to finish. A callback can dispose its own
    /// registration without blocking. A registration accepted before cancellation completion is
    /// enlisted in that cancellation operation: its callback, completion, and failure contribute to
    /// the shared cancellation result. Once cancellation completion has closed registration, the
    /// callback starts immediately in a new callback causality scope. It cannot change the completed
    /// shared cancellation result; disposing its returned registration waits for that invocation and
    /// propagates its failure.
    /// </para>
    /// </remarks>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership of the registration is returned to the caller while the context controls invocation.")]
    public ValueTask<IAsyncDisposable> RegisterCancellationCallbackAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        cancellationToken.ThrowIfCancellationRequested();

        CancellationRegistration registration;
        CancellationOperation? callbackOperation = null;
        lock (_lock)
        {
            if (!_cancellationRequested || !_cancellationOperation!.Task.IsCompleted)
            {
                registration = new(this, callback);
                (_registrations ??= []).Add(registration);
                return new(registration);
            }

            registration = new(this, callback, propagateInvocationExceptionOnDispose: true);
            callbackOperation = new(this);
        }

        // Record nested callback causality before the callback can request its caller's cancellation.
        _ = CancellationOperation.TryAddDependency(CancellationOperation.Current, callbackOperation!);
        _ = InvokePostCompletionCallbackAsync(callbackOperation!, registration);
        return new(registration);
    }

    internal Task RequestCancellationAsync(string? reason, CancellationToken cancellationToken)
    {
        CancellationOperation operation;
        lock (_lock)
        {
            if (!_cancellationRequested)
            {
                _cancellationReason = reason;
                _cancellationRequested = true;
            }

            operation = _cancellationOperation ??= new(this);
        }

        var waitForCompletion = CancellationOperation.TryAddDependency(
            CancellationOperation.Current,
            operation);
        operation.Start();
        if (!waitForCompletion)
        {
            return Task.CompletedTask;
        }

        return cancellationToken.CanBeCanceled
            ? operation.Task.WaitAsync(cancellationToken)
            : operation.Task;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every cancellation observer failure is aggregated into the durable cancellation result.")]
    [SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "CancellationTokenSource.Cancel is intentionally synchronous so ordinary token observers run before durable callbacks.")]
    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "CancellationTokenSource.Cancel can populate the exception list by throwing AggregateException.")]
    private async Task CompleteCancellationAsync(CancellationOperation operation)
    {
        List<Exception>? exceptions = null;
        using (operation.Enter())
        {
            try
            {
                using (operation.Detach())
                {
                    _cancellationSource.Cancel();
                }
            }
            catch (AggregateException exception)
            {
                (exceptions ??= []).AddRange(exception.InnerExceptions);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }

            while (true)
            {
                List<CancellationRegistration> callbacks;
                lock (_lock)
                {
                    if (_registrations is not { Count: > 0 })
                    {
                        CompleteCancellation(operation, exceptions);
                        return;
                    }

                    callbacks = _registrations;
                    _registrations = null;
                }

                foreach (var callback in callbacks)
                {
                    try
                    {
                        using (Enter(this))
                        {
                            await callback.InvokeAsync(CancellationToken).ConfigureAwait(false);
                        }
                    }
                    catch (Exception exception)
                    {
                        (exceptions ??= []).Add(exception);
                    }
                }
            }
        }
    }

    private static void CompleteCancellation(
        CancellationOperation operation,
        List<Exception>? exceptions)
    {
        if (exceptions is not null)
        {
            operation.SetException(new AggregateException(
                "One or more cancellation observers failed.",
                exceptions));
        }
        else
        {
            operation.SetResult();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The registration records post-completion callback failures for observation during disposal.")]
    private async Task InvokePostCompletionCallbackAsync(
        CancellationOperation operation,
        CancellationRegistration registration)
    {
        using (operation.Enter())
        using (Enter(this))
        {
            try
            {
                await registration.InvokeAsync(CancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The registration owns post-completion callback observation.
            }
            finally
            {
                operation.CompleteGraph();
            }
        }
    }

    private void Unregister(CancellationRegistration registration)
    {
        lock (_lock)
        {
            _registrations?.Remove(registration);
        }
    }

    internal static ContextScope Enter(DurableExecutionContext context)
    {
        var previous = AmbientContext.Value;
        AmbientContext.Value = context;
        return new(previous);
    }

    internal readonly struct ContextScope(DurableExecutionContext? previous) : IDisposable
    {
        public void Dispose() => AmbientContext.Value = previous;
    }

    private sealed class CancellationOperation(DurableExecutionContext context)
    {
        private static readonly object GraphLock = new();
        private static readonly AsyncLocal<CancellationOperation?> AmbientOperation = new();
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private HashSet<CancellationOperation>? _dependencies;
        private bool _graphCompleted;
        private int _started;

        public static CancellationOperation? Current => AmbientOperation.Value;

        public Task Task => _completion.Task;

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _ = context.CompleteCancellationAsync(this);
            }
        }

        public static bool TryAddDependency(
            CancellationOperation? source,
            CancellationOperation target)
        {
            if (source is null)
            {
                return true;
            }

            lock (GraphLock)
            {
                if (source._graphCompleted || target._graphCompleted)
                {
                    return true;
                }

                if (ReferenceEquals(source, target) || HasPath(target, source))
                {
                    return false;
                }

                (source._dependencies ??= []).Add(target);
                return true;
            }
        }

        public OperationScope Enter()
        {
            var previous = AmbientOperation.Value;
            AmbientOperation.Value = this;
            return new(previous);
        }

        public OperationScope Detach()
        {
            var previous = AmbientOperation.Value;
            AmbientOperation.Value = null;
            return new(previous);
        }

        public void SetException(Exception exception)
        {
            CompleteGraph();
            _completion.SetException(exception);
        }

        public void SetResult()
        {
            CompleteGraph();
            _completion.SetResult();
        }

        private static bool HasPath(
            CancellationOperation start,
            CancellationOperation destination)
        {
            var pending = new Stack<CancellationOperation>();
            var visited = new HashSet<CancellationOperation>();
            pending.Push(start);
            while (pending.TryPop(out var current))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                if (ReferenceEquals(current, destination))
                {
                    return true;
                }

                if (current._dependencies is { } dependencies)
                {
                    foreach (var dependency in dependencies)
                    {
                        pending.Push(dependency);
                    }
                }
            }

            return false;
        }

        public void CompleteGraph()
        {
            lock (GraphLock)
            {
                _graphCompleted = true;
                _dependencies = null;
            }
        }

        public readonly struct OperationScope(CancellationOperation? previous) : IDisposable
        {
            public void Dispose() => AmbientOperation.Value = previous;
        }
    }

    private sealed class CancellationRegistration : IAsyncDisposable
    {
        private static readonly AsyncLocal<CancellationRegistration?> Current = new();
        private const int Pending = 0;
        private const int Invoking = 1;
        private const int Completed = 2;
        private const int DisposedState = 3;
        private readonly DurableExecutionContext? _context;
        private readonly Func<CancellationToken, ValueTask>? _callback;
        private readonly bool _propagateInvocationExceptionOnDispose;
        private TaskCompletionSource? _completion;
        private Exception? _invocationException;
        private CancellationRegistration? _previous;
        private int _state;

        public CancellationRegistration(
            DurableExecutionContext context,
            Func<CancellationToken, ValueTask> callback,
            bool propagateInvocationExceptionOnDispose = false)
        {
            _context = context;
            _callback = callback;
            _propagateInvocationExceptionOnDispose = propagateInvocationExceptionOnDispose;
        }

        public async ValueTask InvokeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _state, Invoking, Pending) != Pending)
            {
                return;
            }

            var previous = Current.Value;
            _previous = previous;
            Current.Value = this;
            try
            {
                await _callback!(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _invocationException = exception;
                throw;
            }
            finally
            {
                Current.Value = previous;
                _previous = null;
                Interlocked.Exchange(ref _state, Completed);
                CompleteInvocation(Volatile.Read(ref _completion));
            }
        }

        public ValueTask DisposeAsync()
        {
            _context?.Unregister(this);
            while (true)
            {
                switch (Volatile.Read(ref _state))
                {
                    case Pending:
                        if (Interlocked.CompareExchange(ref _state, DisposedState, Pending) == Pending)
                        {
                            return ValueTask.CompletedTask;
                        }

                        break;
                    case Invoking:
                        if (IsCurrentCallback(this))
                        {
                            return ValueTask.CompletedTask;
                        }

                        return new(GetInvocationCompletionTask());
                    case Completed when _propagateInvocationExceptionOnDispose
                        && _invocationException is not null:
                        return new(GetInvocationCompletionTask());
                    case Completed:
                    case DisposedState:
                        return ValueTask.CompletedTask;
                }
            }
        }

        private Task GetInvocationCompletionTask()
        {
            var completion = Volatile.Read(ref _completion);
            if (completion is null)
            {
                var candidate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                completion = Interlocked.CompareExchange(ref _completion, candidate, null) ?? candidate;
            }

            if (Volatile.Read(ref _state) == Completed)
            {
                CompleteInvocation(completion);
            }

            return completion.Task;
        }

        private void CompleteInvocation(TaskCompletionSource? completion)
        {
            if (completion is null)
            {
                return;
            }

            if (_propagateInvocationExceptionOnDispose && _invocationException is { } exception)
            {
                completion.TrySetException(exception);
            }
            else
            {
                completion.TrySetResult();
            }
        }

        private static bool IsCurrentCallback(CancellationRegistration registration)
        {
            for (var current = Current.Value; current is not null; current = current._previous)
            {
                if (ReferenceEquals(current, registration))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

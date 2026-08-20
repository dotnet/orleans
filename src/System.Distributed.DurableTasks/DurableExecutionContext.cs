using System.Globalization;

namespace System.Distributed.DurableTasks;

/// <summary>Supplies host services and deterministic execution state to a durable task.</summary>
public abstract class DurableExecutionContext
{
    private const char GeneratedSegmentPrefix = '$';
    private static readonly AsyncLocal<DurableExecutionContext?> AmbientContext = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancellationSource = new();
    private List<CancellationRegistration>? _registrations;
    private CancellationOperation? _cancellationOperation;
    private bool _cancellationRequested;
    private long _nextChildId;
    private long _nextOperationId;

    protected DurableExecutionContext(TaskId taskId)
    {
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable execution requires an explicit task identifier.", nameof(taskId));
        }

        TaskId = taskId;
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
    /// Gets the durable cancellation token for this execution.
    /// </summary>
    /// <remarks>
    /// Registrations on this token are ordinary synchronous .NET cleanup observers only. They follow
    /// standard <see cref="CancellationToken"/> behavior, including registration-time
    /// <see cref="ExecutionContext"/> capture, optional <see cref="SynchronizationContext"/> dispatch,
    /// and current execution-context behavior for unsafe registrations.
    ///
    /// <para>
    /// These observers must return promptly. They must not call, block on, await, or otherwise
    /// orchestrate <see cref="DurableTaskRuntimeHelper.RequestCancellationAsync"/> or durable
    /// cancellation of any context. Doing so is outside this contract and has the usual synchronous
    /// reentrancy and sync-over-async risks. Use <see cref="RegisterCancellationCallbackAsync"/> for
    /// all cancellation work which requests another durable context, awaits asynchronous work, needs
    /// cycle handling, or participates in durable failure aggregation.
    /// </para>
    /// </remarks>
    public CancellationToken CancellationToken => _cancellationSource.Token;

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
    /// Returns the first completed candidate and persists that selection under <paramref name="decisionId"/>.
    /// Repeated calls with the same decision identifier return the recorded winner.
    /// </summary>
    protected internal abstract ValueTask<TaskId> SelectCompletionAsync(
        TaskId decisionId,
        IReadOnlyList<TaskId> candidates,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a replay-stable child identifier, using <paramref name="name"/> when supplied.
    /// Explicit names beginning with <c>$</c> are reserved for generated identifiers.
    /// </summary>
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

        _ = InvokePostCompletionCallbackAsync(callbackOperation!, registration);
        return new(registration);
    }

    internal Task RequestCancellationAsync(CancellationToken cancellationToken)
    {
        CancellationOperation operation;
        lock (_lock)
        {
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

            lock (_lock)
            {
                _cancellationRequested = true;
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
                            await callback.InvokeAsync(CancellationToken);
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

    private async Task InvokePostCompletionCallbackAsync(
        CancellationOperation operation,
        CancellationRegistration registration)
    {
        using (operation.Enter())
        using (Enter(this))
        {
            try
            {
                await registration.InvokeAsync(CancellationToken);
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
                await _callback!(cancellationToken);
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
                Volatile.Write(ref _state, Completed);
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

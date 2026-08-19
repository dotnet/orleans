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
    /// Registrations on this token are ordinary synchronous .NET cancellation observers. They run
    /// under the execution context captured when they are registered and, when requested by the
    /// registration overload, its synchronization context. They must return promptly and must not
    /// synchronously wait for <see cref="DurableTaskRuntimeHelper.RequestCancellationAsync"/> or
    /// another durable cancellation operation. Use <see cref="RegisterCancellationCallbackAsync"/>
    /// for asynchronous callbacks, durable cancellation dependencies, and failure aggregation.
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
    /// The callback executes with this durable context as <see cref="Current"/> and participates in
    /// durable cancellation dependency tracking and failure aggregation. Its cancellation operation
    /// flows with <see cref="ExecutionContext"/> across ordinary awaits and safe thread-pool dispatch,
    /// including <see cref="Task.Run(Action)"/>. Suppressing execution-context flow or using an unsafe
    /// dispatch API detaches that work, so cancellation requests made by it are external observers
    /// rather than durable dependencies.
    ///
    /// <para>
    /// Disposing the returned registration prevents an invocation which has not started, or
    /// asynchronously waits for an active invocation to finish. A callback can dispose its own
    /// registration without blocking.
    /// </para>
    /// </remarks>
    public async ValueTask<IAsyncDisposable> RegisterCancellationCallbackAsync(
        Func<CancellationToken, ValueTask> callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        CancellationRegistration? registration;
        lock (_lock)
        {
            if (!_cancellationRequested)
            {
                registration = new(this, callback);
                (_registrations ??= []).Add(registration);
                return registration;
            }

            registration = null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        using (Enter(this))
        {
            await callback(CancellationToken);
        }

        return CancellationRegistration.Disposed;
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

            List<CancellationRegistration> callbacks;
            lock (_lock)
            {
                _cancellationRequested = true;
                callbacks = _registrations ?? [];
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

        if (exceptions is not null)
        {
            operation.SetException(new AggregateException(
                "One or more durable cancellation callbacks failed.",
                exceptions));
        }
        else
        {
            operation.SetResult();
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

        private void CompleteGraph()
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
        private TaskCompletionSource? _completion;
        private CancellationRegistration? _previous;
        private int _state;

        private CancellationRegistration(bool disposed) => _state = disposed ? DisposedState : Pending;

        public CancellationRegistration(
            DurableExecutionContext context,
            Func<CancellationToken, ValueTask> callback)
        {
            _context = context;
            _callback = callback;
        }

        public static CancellationRegistration Disposed { get; } = new(true);

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
            finally
            {
                Current.Value = previous;
                _previous = null;
                Volatile.Write(ref _state, Completed);
                Volatile.Read(ref _completion)?.TrySetResult();
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
                completion.TrySetResult();
            }

            return completion.Task;
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

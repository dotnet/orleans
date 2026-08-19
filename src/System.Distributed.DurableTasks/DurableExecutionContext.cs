using System.Globalization;

namespace System.Distributed.DurableTasks;

/// <summary>Supplies host services and deterministic execution state to a durable task.</summary>
public abstract class DurableExecutionContext
{
    private static readonly AsyncLocal<DurableExecutionContext?> AmbientContext = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancellationSource = new();
    private List<CancellationRegistration>? _registrations;
    private Task? _cancellationTask;
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

    /// <summary>Gets the durable cancellation token for this execution.</summary>
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

    /// <summary>Creates a replay-stable child identifier, using <paramref name="name"/> when supplied.</summary>
    protected internal virtual TaskId CreateChildTaskId(string? name)
        => TaskId.Child(name ?? Interlocked.Increment(ref _nextChildId).ToString(CultureInfo.InvariantCulture));

    internal TaskId CreateOperationId(string kind)
        => TaskId.Child($"${kind}-{Interlocked.Increment(ref _nextOperationId).ToString(CultureInfo.InvariantCulture)}");

    /// <summary>Registers a callback which observes the durable cancellation request.</summary>
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
        Task cancellationTask;
        List<CancellationRegistration>? callbacks = null;
        TaskCompletionSource? completion = null;
        lock (_lock)
        {
            if (_cancellationTask is null)
            {
                _cancellationRequested = true;
                callbacks = _registrations ?? [];
                _registrations = null;
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                cancellationTask = _cancellationTask = completion.Task;
            }
            else
            {
                cancellationTask = _cancellationTask;
            }
        }

        if (completion is not null)
        {
            _ = CompleteCancellationAsync(callbacks!, completion);
        }

        return cancellationToken.CanBeCanceled
            ? cancellationTask.WaitAsync(cancellationToken)
            : cancellationTask;
    }

    private async Task CompleteCancellationAsync(
        List<CancellationRegistration> callbacks,
        TaskCompletionSource completion)
    {
        List<Exception>? exceptions = null;
        using (Enter(this))
        {
            try
            {
                _cancellationSource.Cancel();
            }
            catch (AggregateException exception)
            {
                (exceptions ??= []).AddRange(exception.InnerExceptions);
            }
            catch (Exception exception)
            {
                (exceptions ??= []).Add(exception);
            }
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

        if (exceptions is not null)
        {
            completion.SetException(new AggregateException(
                "One or more durable cancellation callbacks failed.",
                exceptions));
        }
        else
        {
            completion.SetResult();
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

    private sealed class CancellationRegistration(
        DurableExecutionContext? context,
        Func<CancellationToken, ValueTask>? callback) : IAsyncDisposable
    {
        public static CancellationRegistration Disposed { get; } = new(null, null);

        public ValueTask InvokeAsync(CancellationToken cancellationToken)
            => callback is null ? ValueTask.CompletedTask : callback(cancellationToken);

        public ValueTask DisposeAsync()
        {
            context?.Unregister(this);
            return ValueTask.CompletedTask;
        }
    }
}

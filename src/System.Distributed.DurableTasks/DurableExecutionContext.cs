namespace System.Distributed.DurableTasks;

public abstract partial class DurableExecutionContext(TaskId id)
{
    private static readonly AsyncLocal<DurableExecutionContext?> Current = new();
    private readonly object _lockObj = new();
    protected object SyncRoot => _lockObj;

    private List<CancellationCallbackRegistrationBase>? _cancellationCallbacks;
    private List<CancellationCallbackRegistrationBase>? _deactivationCallbacks;
    private bool _cancellationSignaled;
    private bool _deactivationSignaled;

    public static DurableExecutionContext? CurrentContext => Current.Value;

    internal static void SetCurrentContext(DurableExecutionContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableExecutionContext? context, out DurableExecutionContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    internal static Action WrapContinuation(Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var context = Current.Value;
        var executionContext = ExecutionContext.Capture();
        Action wrapped = () =>
        {
            SetCurrentContext(context, out var previous);
            try
            {
                continuation();
            }
            finally
            {
                SetCurrentContext(previous);
            }
        };
        wrapped = context?.WrapContinuationCore(wrapped) ?? wrapped;
        return executionContext is null
            ? wrapped
            : () => ExecutionContext.Run(executionContext, static state => ((Action)state!)(), wrapped);
    }

    public TaskId TaskId { get; } = id;

    protected internal abstract ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    protected internal abstract ValueTask<DurableTaskResponse> ScheduleDelayAsync(TaskId taskId, DateTimeOffset dueTime, CancellationToken cancellationToken);
    protected internal abstract IScheduledTaskHandle GetChildTaskHandle(TaskId taskId);
    protected internal abstract TaskId CreateChildTaskId(string? name);
    protected internal virtual DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
    protected internal virtual bool SupportsTaskDelegates => true;

    internal void EnsureTaskDelegateSupported(Delegate callback)
    {
        if (!SupportsTaskDelegates)
        {
            throw new InvalidOperationException(
                "Async Task-returning delegates are not supported in an isolated durable execution context. "
                + "Use a method which returns DurableTask instead.");
        }
    }
    internal virtual Action WrapContinuationCore(Action continuation) => continuation;

    // Note that blocking on cancellation of a task from within that task would result in a deadlock
    // Cancels the task if it is scheduled or running. If the task is not scheduled or running, this method does nothing.
    internal async Task CancelAsync(CancellationToken cancellationToken)
        => await InvokeCallbacksAsync(isDeactivation: false, cancellationToken);

    protected internal async Task DeactivateAsync(CancellationToken cancellationToken)
        => await InvokeCallbacksAsync(isDeactivation: true, cancellationToken);

    private async Task InvokeCallbacksAsync(bool isDeactivation, CancellationToken cancellationToken)
    {
        List<CancellationCallbackRegistrationBase>? callbacks;
        lock (_lockObj)
        {
            if (isDeactivation)
            {
                _deactivationSignaled = true;
                callbacks = _deactivationCallbacks;
                _deactivationCallbacks = null;
            }
            else
            {
                _cancellationSignaled = true;
                callbacks = _cancellationCallbacks;
                _cancellationCallbacks = null;
            }
        }

        if (callbacks is not null)
        {
            List<Exception>? exceptions = null;
            foreach (var callback in callbacks)
            {
                try
                {
                    await callback.InvokeAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    (exceptions ??= []).Add(exception);
                }
            }

            if (exceptions is not null)
            {
                throw new AggregateException("One or more durable task cancellation callbacks failed.", exceptions);
            }
        }
    }

    public IDisposable RegisterCancellationCallback<TState>(Func<TState, CancellationToken, Task> callback, TState state) =>
        RegisterCallbackCore(new CancellationCallbackRegistration<TState>(callback, state, this, isDeactivation: false));

    public IDisposable RegisterCancellationCallback(Func<CancellationToken, Task> callback) =>
        RegisterCallbackCore(new CancellationCallbackRegistration(callback, this, isDeactivation: false));

    /// <summary>
    /// Registers cleanup which aborts activation-owned execution during deactivation without propagating durable cancellation.
    /// </summary>
    /// <typeparam name="TState">The callback state type.</typeparam>
    /// <param name="callback">The asynchronous cleanup callback.</param>
    /// <param name="state">The callback state.</param>
    /// <returns>A registration which removes the callback when disposed.</returns>
    public IDisposable RegisterDeactivationCallback<TState>(Func<TState, CancellationToken, Task> callback, TState state) =>
        RegisterCallbackCore(new CancellationCallbackRegistration<TState>(callback, state, this, isDeactivation: true));

    internal IDisposable RegisterCancellationTokenSource(CancellationTokenSource cancellationTokenSource)
    {
        var cancellation = RegisterCancellationCallback(
            static (source, _) => source.CancelAsync(),
            cancellationTokenSource);
        try
        {
            return new CompositeRegistration(
                cancellation,
                RegisterDeactivationCallback(
                    static (source, _) => source.CancelAsync(),
                    cancellationTokenSource));
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    private CancellationCallbackRegistrationBase RegisterCallbackCore(CancellationCallbackRegistrationBase callback)
    {
        lock (_lockObj)
        {
            if (callback.IsDeactivation)
            {
                if (_deactivationSignaled)
                {
                    throw new OperationCanceledException("The durable execution context is deactivating.");
                }

                (_deactivationCallbacks ??= []).Add(callback);
            }
            else
            {
                if (_cancellationSignaled)
                {
                    throw new OperationCanceledException("The durable execution context has been canceled.");
                }

                (_cancellationCallbacks ??= []).Add(callback);
            }

            return callback;
        }
    }

    private abstract class CancellationCallbackRegistrationBase(DurableExecutionContext context, bool isDeactivation) : IDisposable
    {
        private bool _disposed;

        public bool IsDeactivation { get; } = isDeactivation;

        public Task InvokeAsync(CancellationToken cancellationToken)
        {
            lock (context.SyncRoot)
            {
                if (_disposed)
                {
                    return Task.CompletedTask;
                }

                return InvokeCore(cancellationToken);
            }
        }

        public void Dispose()
        {
            context.UnregisterCancellationCallback(this);
        }

        public void MarkDisposed() => _disposed = true;

        protected abstract Task InvokeCore(CancellationToken cancellationToken);
    }

    private sealed class CancellationCallbackRegistration<TState>(
        Func<TState, CancellationToken, Task> callback,
        TState state,
        DurableExecutionContext context,
        bool isDeactivation) : CancellationCallbackRegistrationBase(context, isDeactivation)
    {
        protected override Task InvokeCore(CancellationToken cancellationToken) => callback(state, cancellationToken);
    }

    private sealed class CancellationCallbackRegistration(
        Func<CancellationToken, Task> callback,
        DurableExecutionContext context,
        bool isDeactivation) : CancellationCallbackRegistrationBase(context, isDeactivation)
    {
        protected override Task InvokeCore(CancellationToken cancellationToken) => callback(cancellationToken);
    }

    private void UnregisterCancellationCallback(CancellationCallbackRegistrationBase registration)
    {
        lock (_lockObj)
        {
            registration.MarkDisposed();
            if (registration.IsDeactivation)
            {
                _deactivationCallbacks?.Remove(registration);
            }
            else
            {
                _cancellationCallbacks?.Remove(registration);
            }
        }
    }

    private sealed class CompositeRegistration(IDisposable cancellation, IDisposable deactivation) : IDisposable
    {
        public void Dispose()
        {
            cancellation.Dispose();
            deactivation.Dispose();
        }
    }

}

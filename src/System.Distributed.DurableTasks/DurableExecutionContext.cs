namespace System.Distributed.DurableTasks;

public abstract partial class DurableExecutionContext(TaskId id)
{
    private static readonly AsyncLocal<DurableExecutionContext?> Current = new();
    private readonly object _lockObj = new();
    protected object SyncRoot => _lockObj;

    private List<CancellationCallbackRegistrationBase>? _cancellationCallbacks;

    public static DurableExecutionContext? CurrentContext => Current.Value;

    internal static void SetCurrentContext(DurableExecutionContext? context) => Current.Value = context;
    internal static void SetCurrentContext(DurableExecutionContext? context, out DurableExecutionContext? previous)
    {
        previous = Current.Value;
        Current.Value = context;
    }

    public TaskId TaskId { get; } = id;

    protected internal abstract ValueTask<IScheduledTaskHandle> ScheduleChildTaskAsync(TaskId taskId, DurableTask taskDefinition, CancellationToken cancellationToken);
    protected internal abstract IScheduledTaskHandle GetChildTaskHandle(TaskId taskId);
    protected internal abstract TaskId CreateChildTaskId(string? name);

    // Note that blocking on cancellation of a task from within that task would result in a deadlock
    // Cancels the task if it is scheduled or running. If the task is not scheduled or running, this method does nothing.
    internal async Task CancelAsync(CancellationToken cancellationToken)
    {
        List<CancellationCallbackRegistrationBase>? callbacks;
        lock (_lockObj)
        {
            callbacks = _cancellationCallbacks;
            _cancellationCallbacks = null;
        }

        if (callbacks is not null)
        {
            foreach (var callback in callbacks)
            {
                await callback.InvokeAsync(cancellationToken);
            }
        }
    }

    public IDisposable RegisterCancellationCallback<TState>(Func<TState, CancellationToken, Task> callback, TState state) => RegisterCancellationCallbackCore(new CancellationCallbackRegistration<TState>(callback, state, this));

    public IDisposable RegisterCancellationCallback(Func<CancellationToken, Task> callback) => RegisterCancellationCallbackCore(new CancellationCallbackRegistration(callback, this));

    private CancellationCallbackRegistrationBase RegisterCancellationCallbackCore(CancellationCallbackRegistrationBase callback)
    {
        lock (_lockObj)
        {
            (_cancellationCallbacks ??= []).Add(callback);
            return callback;
        }
    }

    private abstract class CancellationCallbackRegistrationBase(DurableExecutionContext context) : IDisposable
    {
        public abstract Task InvokeAsync(CancellationToken cancellationToken);

        public void Dispose()
        {
            context.UnregisterCancellationCallback(this);
        }
    }

    private sealed class CancellationCallbackRegistration<TState>(Func<TState, CancellationToken, Task> callback, TState state, DurableExecutionContext context) : CancellationCallbackRegistrationBase(context)
    {
        public override Task InvokeAsync(CancellationToken cancellationToken) => callback(state, cancellationToken);
    }

    private sealed class CancellationCallbackRegistration(Func<CancellationToken, Task> callback, DurableExecutionContext context) : CancellationCallbackRegistrationBase(context)
    {
        public override Task InvokeAsync(CancellationToken cancellationToken) => callback(cancellationToken);
    }

    private void UnregisterCancellationCallback(CancellationCallbackRegistrationBase registration)
    {
        if (_cancellationCallbacks is null)
        {
            return;
        }

        lock (_lockObj)
        {
            _cancellationCallbacks.Remove(registration);
        }
    }
}

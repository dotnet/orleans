using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Provides an awaiter for <see cref="DurableTask"/> instances.
/// </summary>
public readonly struct DurableTaskAwaiter : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly TaskAwaiter<DurableTaskResponse> _awaiter;

    internal DurableTaskAwaiter(Task<DurableTaskResponse> invokedTask)
    {
        _awaiter = invokedTask.GetAwaiter();
    }

    public void GetResult() => _awaiter.GetResult().ThrowIfExceptionResponse();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}

/// <summary>
/// Provides an awaiter for <see cref="DurableTask{TResult}"/> instances.
/// </summary>
public readonly struct DurableTaskAwaiter<TResult> : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly TaskAwaiter<DurableTaskResponse> _awaiter;

    internal DurableTaskAwaiter(Task<DurableTaskResponse> invokedTask)
    {
        _awaiter = invokedTask.GetAwaiter();
    }

    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}

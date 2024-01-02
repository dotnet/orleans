using System.Runtime.CompilerServices;

namespace System.Distributed.DurableTasks;

/*
public readonly struct DurableTaskResultAwaitable<TResult>(DurableExecutionContext executionContext)
{
    private readonly DurableExecutionContext _executionContext = executionContext;

    public DurableTaskResultAwaiter<TResult> GetAwaiter() => new(_executionContext.ResponseTask);
}

public readonly struct DurableTaskResultAwaiter<TResult> : INotifyCompletion, ICriticalNotifyCompletion
{
    private readonly TaskAwaiter<DurableTaskResponse> _awaiter;

    internal DurableTaskResultAwaiter(Task<DurableTaskResponse> responseTask)
    {
        _awaiter = responseTask.GetAwaiter();
    }

    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
    public bool IsCompleted => _awaiter.IsCompleted;
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);
}
*/

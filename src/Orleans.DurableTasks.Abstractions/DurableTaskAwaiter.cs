using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Orleans.DurableTasks;

/// <summary>Provides an awaiter for a durable task without a result.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Awaiters are compiler protocol values whose identity and equality are not part of their contract.")]
public readonly struct DurableTaskAwaiter : ICriticalNotifyCompletion
{
    private readonly TaskAwaiter<DurableTaskResponse> _awaiter;

    internal DurableTaskAwaiter(Task<DurableTaskResponse> task) => _awaiter = task.GetAwaiter();

    /// <inheritdoc />
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);

    /// <summary>Returns after successful completion.</summary>
    public void GetResult() => _awaiter.GetResult().EnsureSuccessfulCompletion();
}

/// <summary>Provides an awaiter for a durable task with a result.</summary>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Awaiters are compiler protocol values whose identity and equality are not part of their contract.")]
public readonly struct DurableTaskAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly TaskAwaiter<DurableTaskResponse> _awaiter;

    internal DurableTaskAwaiter(Task<DurableTaskResponse> task) => _awaiter = task.GetAwaiter();

    /// <inheritdoc />
    public bool IsCompleted => _awaiter.IsCompleted;

    /// <inheritdoc />
    public void OnCompleted(Action continuation) => _awaiter.OnCompleted(continuation);

    /// <inheritdoc />
    public void UnsafeOnCompleted(Action continuation) => _awaiter.UnsafeOnCompleted(continuation);

    /// <summary>Returns the successful result.</summary>
    public TResult GetResult() => _awaiter.GetResult().GetResult<TResult>();
}

namespace System.Distributed.DurableTasks;

/// <summary>Provides the host entry points for running and canceling durable definitions.</summary>
public static class DurableTaskRuntimeHelper
{
    /// <summary>Runs <paramref name="task"/> in <paramref name="context"/> and converts terminal exceptions to responses.</summary>
    public static async ValueTask<DurableTaskResponse> RunAsync(DurableTask task, DurableExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);
        using var scope = DurableExecutionContext.Enter(context);
        try
        {
            return await task.RunAsync(context);
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }

    /// <summary>Requests monotonic durable cancellation and waits for callback acknowledgement.</summary>
    /// <remarks>
    /// Repeated requests share one completion. Ordinary token observers run synchronously according
    /// to <see cref="CancellationTokenSource"/> cancellation semantics. Durable callbacks registered
    /// through <see cref="DurableExecutionContext.RegisterCancellationCallbackAsync"/> participate in
    /// durable dependency and failure aggregation. External callers and acyclic durable callback
    /// dependencies wait for completion. If a call made from a durable callback, or asynchronous work
    /// carrying its normally flowed <see cref="ExecutionContext"/>, would close a dependency cycle,
    /// the target request is still initiated but that cycle-closing edge is not awaited. There is no
    /// global or thread-based inference. Durable callbacks registered before the shared completion
    /// closes are enlisted in that operation and contribute to its completion and failures. Callbacks
    /// registered after that atomic boundary run in an independent callback scope and cannot change
    /// this method's already-completed shared result.
    ///
    /// <para>
    /// The <paramref name="cancellationToken"/> only abandons this caller's wait; it does not undo the
    /// durable request. Callbacks registered directly on
    /// <see cref="DurableExecutionContext.CancellationToken"/> are ordinary synchronous cleanup
    /// observers. They must return promptly and must not call, block on, await, or otherwise
    /// orchestrate this method or durable cancellation of any context. Such use is outside the
    /// contract. Use <see cref="DurableExecutionContext.RegisterCancellationCallbackAsync"/> for all
    /// cancellation work which requests another durable context, awaits asynchronous work, needs
    /// cycle handling, or participates in durable failure aggregation.
    /// </para>
    /// </remarks>
    public static Task RequestCancellationAsync(
        DurableExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.RequestCancellationAsync(cancellationToken);
    }
}

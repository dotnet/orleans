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
    /// Repeated requests share one completion and the aggregated failures of its durable and ordinary
    /// cancellation observers. External callers and acyclic durable dependencies wait for that
    /// completion. Dependencies are based only on the cancellation operation explicitly flowed by
    /// <see cref="DurableExecutionContext.RegisterCancellationCallbackAsync"/> and standard
    /// <see cref="ExecutionContext"/> capture, including capture by an ordinary token callback
    /// registered while a durable callback is active. There is no global or thread-based inference.
    /// When an explicitly flowed operation would close a dependency cycle, the target request is
    /// still initiated but that cycle-closing edge is not awaited.
    ///
    /// <para>
    /// The <paramref name="cancellationToken"/> only abandons this caller's wait; it does not undo the
    /// durable request. Callbacks registered directly on
    /// <see cref="DurableExecutionContext.CancellationToken"/> follow standard registration-time
    /// execution-context behavior, but remain synchronous observers: they must return promptly and
    /// must not synchronously wait for this method. Use
    /// <see cref="DurableExecutionContext.RegisterCancellationCallbackAsync"/> for asynchronous work,
    /// awaited cross-context cancellation dependencies, failure aggregation, and clear cycle semantics.
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

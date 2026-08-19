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
    /// completion. When an explicitly flowed durable cancellation operation would close a dependency
    /// cycle, the target request is still initiated but that cycle-closing edge is not awaited.
    ///
    /// <para>
    /// The <paramref name="cancellationToken"/> only abandons this caller's wait; it does not undo the
    /// durable request. Do not synchronously wait for this method from a callback registered directly
    /// on <see cref="DurableExecutionContext.CancellationToken"/>. Such callbacks are ordinary
    /// synchronous .NET observers; asynchronous or cross-context cancellation belongs in
    /// <see cref="DurableExecutionContext.RegisterCancellationCallbackAsync"/>.
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

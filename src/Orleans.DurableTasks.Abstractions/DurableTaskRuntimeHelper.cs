using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableTasks;

/// <summary>Provides the host entry points for running and canceling durable definitions.</summary>
public static class DurableTaskRuntimeHelper
{
    /// <summary>Runs <paramref name="task"/> in <paramref name="context"/> and converts terminal exceptions to responses.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Durable task failures are represented as terminal responses.")]
    public static async ValueTask<DurableTaskResponse> RunAsync(DurableTask task, DurableTaskContext context)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(context);
        using var scope = DurableTaskContext.Enter(context);
        try
        {
            return await task.RunAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            return DurableTaskResponse.FromException(exception);
        }
    }

    /// <summary>
    /// Requests durable cancellation of the task which <paramref name="context"/> runs, carrying <paramref name="reason"/>.
    /// </summary>
    /// <param name="context">The attempt of the task whose cancellation is requested.</param>
    /// <param name="reason">
    /// The reason which <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or <see langword="null"/> for none.
    /// The first request determines the reason, so a host which replays a canceled task passes the reason it recorded
    /// with the task's cancellation.
    /// </param>
    /// <remarks>
    /// The request sets <see cref="DurableTaskContext.IsTaskCancellationRequested"/> and then cancels
    /// <see cref="DurableTaskContext.CancellationToken"/>, whose registrations run synchronously, as they do for any
    /// <see cref="CancellationTokenSource"/>. Later requests change nothing. A host records the request durably before it
    /// calls this, so every later attempt of the task observes it.
    /// </remarks>
    public static void RequestTaskCancellation(DurableTaskContext context, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RequestTaskCancellation(reason);
    }

    /// <summary>Cancels the attempt which <paramref name="context"/> runs, for <paramref name="reason"/>.</summary>
    /// <param name="context">The attempt to cancel.</param>
    /// <param name="reason">Why the host cancels the attempt.</param>
    /// <remarks>
    /// The cancellation cancels <see cref="DurableTaskContext.AttemptCancellationToken"/> and then
    /// <see cref="DurableTaskContext.CancellationToken"/>, whose registrations run synchronously. From then on, the
    /// attempt's durable operations throw <see cref="DurableTaskAttemptCanceledException"/> carrying
    /// <paramref name="reason"/>. Later calls change nothing. The host drains the attempt and discards its response.
    /// </remarks>
    public static void CancelAttempt(DurableTaskContext context, DurableTaskAttemptCancellationReason reason)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancelAttempt(reason);
    }
}

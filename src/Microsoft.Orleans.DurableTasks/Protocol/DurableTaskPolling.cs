using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.DurableTasks.Protocol;

/// <summary>
/// Polls a task's grain from code outside a durable execution, such as a client, retrying the failures which leave the
/// task itself unaffected.
/// </summary>
/// <remarks>
/// <para>
/// A poll is a read, so repeating it is safe. A failure to reach the grain, such as a lost connection, a silo which
/// stopped or crashed, a call which timed out, or an activation which was deactivating, leaves the task running, and the
/// next poll reaches its grain's next activation. Such failures are retried with exponential backoff, from
/// <see cref="InitialRetryDelay"/> to <see cref="MaxRetryDelay"/>, until the poll returns or the caller's token ends the
/// wait. The task's outcome, and <see cref="DurableTaskNotFoundException"/> for a task which the grain doesn't retain,
/// surface at once.
/// </para>
/// <para>
/// A poll which the calling grain serves from its own runtime has no transport to fail, and isn't retried.
/// </para>
/// </remarks>
internal static class DurableTaskPolling
{
    internal static readonly TimeSpan InitialRetryDelay = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>Polls <paramref name="taskId"/> on <paramref name="server"/>, retrying transient failures.</summary>
    public static async ValueTask<DurableTaskResponse> PollAsync(
        IDurableTaskServer server,
        TaskId taskId,
        SubscribeOrPollOptions options,
        CancellationToken cancellationToken)
    {
        if (server is DurableTaskGrainRuntime)
        {
            return await server.SubscribeOrPollAsync(taskId, options, cancellationToken);
        }

        var delay = InitialRetryDelay;
        while (true)
        {
            try
            {
                return await server.SubscribeOrPollAsync(taskId, options, cancellationToken);
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
            }
        }
    }

    /// <summary>
    /// Returns whether <paramref name="exception"/> reports a failure to reach the task's grain, rather than the task's
    /// outcome or the end of the caller's wait.
    /// </summary>
    internal static bool IsTransient(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        // Includes SiloUnavailableException, and rejections of calls to activations which fail or move.
        OrleansMessageRejectionException => true,
        ConnectionFailedException or GatewayTooBusyException or TimeoutException => true,

        // The grain's runtime rejects calls while its activation deactivates.
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        _ => false,
    };
}

using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;
using Orleans.Runtime.Messaging;

namespace Orleans.DurableTasks.Protocol;

/// <summary>
/// Polls a task's grain, and requests its cancellation or abort, from code outside a durable execution, such as a client,
/// retrying the failures which leave the task itself unaffected.
/// </summary>
/// <remarks>
/// <para>
/// A poll is a read, and cancellation and abort are idempotent, so repeating them is safe. A failure to reach the grain,
/// such as a lost connection, a silo which stopped or crashed, a call which timed out, or an activation which was
/// stopping, leaves the task as it was, and the next attempt reaches its grain's next activation. Such failures are
/// retried with exponential backoff, from <see cref="InitialRetryDelay"/> to <see cref="MaxRetryDelay"/>, until the call
/// returns or the caller's token ends the wait. The task's outcome, and <see cref="DurableTaskNotFoundException"/> for a
/// task which the grain doesn't retain, surface at once.
/// </para>
/// <para>
/// A call which the calling grain serves from its own runtime has no transport to fail, and isn't retried.
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
                delay = await BackOffAsync(delay, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="request"/>, an idempotent request to <paramref name="server"/> such as a cancellation or an
    /// abort, retrying transient failures.
    /// </summary>
    /// <remarks>
    /// Each call runs to completion on the grain regardless of <paramref name="cancellationToken"/>, which ends only the
    /// caller's wait, so a request which the grain already received takes effect.
    /// </remarks>
    public static async ValueTask SendAsync<TState>(
        IDurableTaskServer server,
        TState state,
        Func<IDurableTaskServer, TState, ValueTask> request,
        CancellationToken cancellationToken)
    {
        if (server is DurableTaskGrainRuntime)
        {
            await request(server, state).AsTask().WaitAsync(cancellationToken);
            return;
        }

        var delay = InitialRetryDelay;
        while (true)
        {
            try
            {
                await request(server, state).AsTask().WaitAsync(cancellationToken);
                return;
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                delay = await BackOffAsync(delay, cancellationToken);
            }
        }
    }

    /// <summary>Waits <paramref name="delay"/> and returns the next, doubled up to <see cref="MaxRetryDelay"/>.</summary>
    private static async ValueTask<TimeSpan> BackOffAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        await Task.Delay(delay, cancellationToken);
        return TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxRetryDelay.Ticks));
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

        // The grain's runtime rejects calls, and fails pending waits, while its activation stops.
        DurableTaskAttemptCanceledException { Reason: DurableTaskAttemptCancellationReason.ActivationStopping } => !cancellationToken.IsCancellationRequested,
        _ => false,
    };
}

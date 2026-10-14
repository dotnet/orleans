using System.Threading;
using System.Threading.Tasks;

namespace Orleans.DurableTasks;

/// <summary>
/// Observes and controls the durable tasks of the current durable grain activation, without a grain call.
/// </summary>
/// <remarks>
/// Durable grains, which derive from <see cref="Orleans.Journaling.DurableGrain"/> or implement
/// <see cref="Orleans.Messaging.IMessagingGrain"/>, resolve the manager from their activation services, for example
/// through constructor injection. Each activation has its own manager, whose operations run against that activation's
/// durable task runtime. Call it from the activation, such as from the grain's methods and durable tasks.
/// </remarks>
public interface IDurableTaskManager
{
    /// <summary>Gets a handle which observes or cancels an existing task of this grain.</summary>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>
    /// A handle for the task. Polling or waiting for a task which this grain doesn't retain throws
    /// <see cref="DurableTaskNotFoundException"/>.
    /// </returns>
    ScheduledTask GetTask(TaskId taskId);

    /// <summary>Gets a typed handle which observes or cancels an existing task of this grain.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>
    /// A handle for the task. Polling or waiting for a task which this grain doesn't retain throws
    /// <see cref="DurableTaskNotFoundException"/>.
    /// </returns>
    ScheduledTask<TResult> GetTask<TResult>(TaskId taskId);

    /// <summary>Durably requests cancellation of a task of this grain and its execution tree.</summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <returns>An operation which completes once the cancellation request has committed and propagated.</returns>
    /// <remarks>
    /// The request is monotonic and idempotent, and a terminal task is unchanged. A running task observes the request
    /// through its durable cancellation token and decides its own outcome; the operation doesn't wait for it to finish.
    /// A request for a task which doesn't exist yet is recorded, so the task terminalizes as canceled when its
    /// invocation arrives.
    /// </remarks>
    ValueTask CancelAsync(TaskId taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Administratively aborts a task of this grain: commits the task and its execution tree as failed with
    /// <see cref="DurableTaskAbortedException"/> in one write, and stops their running attempts without running further
    /// task code.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="reason">The reason for the abort, recorded as the exception message.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the abort to commit.</param>
    /// <returns>An operation which completes once the abort has committed.</returns>
    /// <remarks>
    /// Pending calls to other grains in the tree receive an abort of their own tasks. A task which is terminal, or which
    /// this grain doesn't retain, is unchanged.
    /// </remarks>
    ValueTask AbortAsync(TaskId taskId, string reason, CancellationToken cancellationToken = default);
}

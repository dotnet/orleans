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

    /// <summary>Gets a handle which observes or cancels the existing root task <paramref name="rootId"/> of this grain.</summary>
    /// <param name="rootId">The root task's identifier, as passed to <see cref="TaskId.CreateRoot(string)"/>.</param>
    /// <returns>A handle for the task, as <see cref="GetTask(TaskId)"/> returns.</returns>
    ScheduledTask GetTask(string rootId) => GetTask(TaskId.CreateRoot(rootId));

    /// <summary>Gets a typed handle which observes or cancels an existing task of this grain.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>
    /// A handle for the task. Polling or waiting for a task which this grain doesn't retain throws
    /// <see cref="DurableTaskNotFoundException"/>.
    /// </returns>
    ScheduledTask<TResult> GetTask<TResult>(TaskId taskId);

    /// <summary>Gets a typed handle which observes or cancels the existing root task <paramref name="rootId"/> of this grain.</summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="rootId">The root task's identifier, as passed to <see cref="TaskId.CreateRoot(string)"/>.</param>
    /// <returns>A handle for the task, as <see cref="GetTask{TResult}(TaskId)"/> returns.</returns>
    ScheduledTask<TResult> GetTask<TResult>(string rootId) => GetTask<TResult>(TaskId.CreateRoot(rootId));

    /// <summary>Durably requests cancellation of a task of this grain and its execution tree.</summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <returns>An operation which completes once the cancellation request has committed and propagated.</returns>
    /// <remarks>This request carries no reason; otherwise it behaves as <see cref="CancelAsync(TaskId, string?, CancellationToken)"/>.</remarks>
    ValueTask CancelAsync(TaskId taskId, CancellationToken cancellationToken = default)
        => CancelAsync(taskId, reason: null, cancellationToken);

    /// <summary>Durably requests cancellation of a task of this grain and its execution tree, carrying <paramref name="reason"/>.</summary>
    /// <param name="taskId">The task identifier.</param>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or
    /// <see langword="null"/> for none.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <returns>An operation which completes once the cancellation request has committed and propagated.</returns>
    /// <remarks>
    /// The request is monotonic and idempotent, and a terminal task is unchanged. The first request determines the reason,
    /// which the task and its execution tree record with the request and which reaches the tree's calls to other grains.
    /// A running task observes the request through its durable cancellation token and decides its own outcome; the
    /// operation doesn't wait for it to finish. A request for a task which doesn't exist yet is recorded, so the task
    /// terminalizes as canceled when its invocation arrives.
    /// </remarks>
    ValueTask CancelAsync(TaskId taskId, string? reason, CancellationToken cancellationToken = default);

    /// <summary>Durably requests cancellation of the root task <paramref name="rootId"/> of this grain and its execution tree.</summary>
    /// <param name="rootId">The root task's identifier, as passed to <see cref="TaskId.CreateRoot(string)"/>.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <returns>An operation which completes once the cancellation request has committed and propagated.</returns>
    /// <remarks>This request carries no reason; otherwise it behaves as <see cref="CancelAsync(TaskId, string?, CancellationToken)"/>.</remarks>
    ValueTask CancelAsync(string rootId, CancellationToken cancellationToken = default)
        => CancelAsync(TaskId.CreateRoot(rootId), reason: null, cancellationToken);

    /// <summary>
    /// Durably requests cancellation of the root task <paramref name="rootId"/> of this grain and its execution tree,
    /// carrying <paramref name="reason"/>.
    /// </summary>
    /// <param name="rootId">The root task's identifier, as passed to <see cref="TaskId.CreateRoot(string)"/>.</param>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.TaskCancellationReason"/> reports, or
    /// <see langword="null"/> for none.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    /// <returns>An operation which completes once the cancellation request has committed and propagated.</returns>
    /// <remarks>The request behaves as <see cref="CancelAsync(TaskId, string?, CancellationToken)"/>.</remarks>
    ValueTask CancelAsync(string rootId, string? reason, CancellationToken cancellationToken = default)
        => CancelAsync(TaskId.CreateRoot(rootId), reason, cancellationToken);

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

    /// <summary>Administratively aborts the root task <paramref name="rootId"/> of this grain and its execution tree.</summary>
    /// <param name="rootId">The root task's identifier, as passed to <see cref="TaskId.CreateRoot(string)"/>.</param>
    /// <param name="reason">The reason for the abort, recorded as the exception message.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the abort to commit.</param>
    /// <returns>An operation which completes once the abort has committed.</returns>
    /// <remarks>The abort behaves as <see cref="AbortAsync(TaskId, string, CancellationToken)"/>.</remarks>
    ValueTask AbortAsync(string rootId, string reason, CancellationToken cancellationToken = default)
        => AbortAsync(TaskId.CreateRoot(rootId), reason, cancellationToken);

    /// <summary>Delivers <paramref name="signal"/> with <paramref name="value"/> to an invoked task of this grain.</summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="taskId">The identifier of a task which a client or another grain invoked on this grain.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the signal to commit.</param>
    /// <returns>An operation which completes once the signal commits with this grain's pending state changes.</returns>
    /// <remarks>
    /// The task records the first value for each signal name. A signal with an equal value leaves the task unchanged.
    /// Within a durable execution, the send is recorded in the execution's tree under an identifier derived from the task
    /// and the signal name, so a replay leaves the task unchanged; repeated sends of one signal in an execution are
    /// numbered by occurrence. <see cref="TrySignalAsync{T}(TaskId, DurableSignal{T}, T, CancellationToken)"/>
    /// reports a rejection as its result instead of throwing.
    /// </remarks>
    /// <exception cref="DurableTaskNotFoundException">This grain retains no invoked task <paramref name="taskId"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// The task holds a different value for the signal, or it is terminal and holds no value for the signal.
    /// </exception>
    ValueTask SignalAsync<T>(TaskId taskId, DurableSignal<T> signal, T value, CancellationToken cancellationToken = default);

    /// <summary>Delivers <paramref name="signal"/> with <paramref name="value"/> to the invoked root task <paramref name="rootId"/> of this grain.</summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="rootId">The root task's identifier, as passed to <see cref="TaskId.CreateRoot(string)"/>.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the signal to commit.</param>
    /// <returns>An operation which completes once the signal commits with this grain's pending state changes.</returns>
    /// <remarks>The signal behaves as <see cref="SignalAsync{T}(TaskId, DurableSignal{T}, T, CancellationToken)"/>.</remarks>
    ValueTask SignalAsync<T>(string rootId, DurableSignal<T> signal, T value, CancellationToken cancellationToken = default)
        => SignalAsync(TaskId.CreateRoot(rootId), signal, value, cancellationToken);

    /// <summary>
    /// Delivers <paramref name="signal"/> with <paramref name="value"/> to an invoked task of this grain, and returns how
    /// the task handled it.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="taskId">The identifier of a task which a client or another grain invoked on this grain.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the signal to commit.</param>
    /// <returns>
    /// <see cref="DurableSignalResult.Accepted"/> or <see cref="DurableSignalResult.Duplicate"/> once the task durably
    /// holds the value, or the reason the task rejected it, which changes nothing.
    /// </returns>
    /// <remarks>
    /// The signal behaves as <see cref="SignalAsync{T}(TaskId, DurableSignal{T}, T, CancellationToken)"/>, which throws
    /// for the rejections this method returns. Within a durable execution, the send and its result are recorded in the
    /// execution's tree, so a replay returns the same result.
    /// </remarks>
    ValueTask<DurableSignalResult> TrySignalAsync<T>(TaskId taskId, DurableSignal<T> signal, T value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delivers <paramref name="signal"/> with <paramref name="value"/> to the invoked root task <paramref name="rootId"/>
    /// of this grain, and returns how the task handled it.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="rootId">The root task's identifier, as passed to <see cref="TaskId.CreateRoot(string)"/>.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the signal to commit.</param>
    /// <returns>The task's handling of the signal, as <see cref="TrySignalAsync{T}(TaskId, DurableSignal{T}, T, CancellationToken)"/> returns.</returns>
    ValueTask<DurableSignalResult> TrySignalAsync<T>(string rootId, DurableSignal<T> signal, T value, CancellationToken cancellationToken = default)
        => TrySignalAsync(TaskId.CreateRoot(rootId), signal, value, cancellationToken);
}

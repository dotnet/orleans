using System;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.DurableTasks.Runtime;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

/// <summary>
/// Provides operations for observing and signaling existing durable tasks on grain references.
/// </summary>
public static class DurableTaskGrainExtensions
{
    /// <summary>
    /// Delivers <paramref name="signal"/> with <paramref name="value"/> to the invoked task <paramref name="taskId"/> on
    /// <paramref name="target"/>.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="target">The grain which owns the task.</param>
    /// <param name="taskId">The identifier of a task which a client or another grain invoked on <paramref name="target"/>.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons the send.</param>
    /// <returns>An operation which completes once the send is durable.</returns>
    /// <remarks>
    /// <para>
    /// The task records the first value for each signal name, and a signal with an equal value leaves it unchanged.
    /// </para>
    /// <para>
    /// Within a durable execution, the send and a record of it in the execution's tree commit in one journal write of
    /// the sending grain, so a replay doesn't send it again; sends begin in deterministic order, like unnamed children.
    /// A send to another grain is staged in the outbox, and the target's inbox records the value in task state or
    /// dead-letters a rejected signal. Elsewhere, such as from a client or an ordinary grain method, the send is a call
    /// to <paramref name="target"/> which completes once the task durably holds the value and throws when the task
    /// rejects it. A grain signals its own tasks from ordinary methods through <see cref="IDurableTaskManager"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> or <paramref name="signal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is empty.</exception>
    /// <exception cref="DurableTaskNotFoundException">Outside a durable execution, the target retains no invoked task <paramref name="taskId"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Outside a durable execution, the task holds a different value for the signal, or it is terminal and holds no value
    /// for the signal.
    /// </exception>
    public static ValueTask SignalDurableTaskAsync<T>(
        this IAddressable target,
        TaskId taskId,
        DurableSignal<T> signal,
        T value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(signal);
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A signal requires a task identifier.", nameof(taskId));
        }

        var response = DurableTaskResponse.FromResult(value);
        return DurableExecutionContext.Current is GrainDurableExecutionContext context
            ? context.SendSignalAsync(target.GetGrainId(), taskId, signal.Name, response, cancellationToken)
            : target.AsReference<IDurableTaskGrainExtension>().SignalAsync(taskId, signal.Name, response, cancellationToken);
    }

    /// <summary>
    /// Gets a typed handle which observes an existing root durable task without scheduling it.
    /// </summary>
    /// <typeparam name="TResult">The durable task result type.</typeparam>
    /// <param name="target">The grain which owns the durable task.</param>
    /// <param name="rootId">The stable root task identifier.</param>
    /// <returns>A handle which can poll, wait for, cancel, or abort the existing durable task.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootId"/> is empty or whitespace.</exception>
    public static ScheduledTask<TResult> GetDurableTask<TResult>(this IAddressable target, string rootId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        return new AttachedScheduledTask<TResult>(
            TaskId.CreateRoot(rootId),
            target.AsReference<IDurableTaskGrainExtension>());
    }
}

internal sealed class AttachedScheduledTask<TResult>(
    TaskId taskId,
    IDurableTaskServer server) : ScheduledTask<TResult>
{
    private readonly GrainScheduledTaskHandle _handle =
        new(taskId, request: null, server, lastResponse: null);

    public override TaskId Id => taskId;

    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) =>
        _handle.CancelAsync(cancellationToken);

    protected override ValueTask AbortAsyncCore(string reason, CancellationToken cancellationToken) =>
        _handle.AbortAsync(reason, cancellationToken);

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(
        PollingOptions options,
        CancellationToken cancellationToken) =>
        _handle.PollAsync(options, cancellationToken);

    protected override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) =>
        _handle.WaitAsync(cancellationToken);
}

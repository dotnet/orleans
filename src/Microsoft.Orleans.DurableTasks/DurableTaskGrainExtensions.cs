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
    /// the sending grain, so a replay doesn't send it again. The record's identifier derives from the target, the task, and
    /// the signal name, and repeated sends of one signal in an execution are numbered by occurrence.
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
        return DurableTaskContext.TryGetCurrent(out var current) && current is GrainDurableTaskContext context
            ? SendFromExecutionAsync(context, target.GetGrainId(), taskId, signal.Name, response, cancellationToken)
            : target.AsReference<IDurableTaskGrainExtension>().SignalAsync(taskId, signal.Name, response, cancellationToken);

        static async ValueTask SendFromExecutionAsync(
            GrainDurableTaskContext context,
            GrainId target,
            TaskId taskId,
            string name,
            DurableTaskResponse response,
            CancellationToken cancellationToken) =>
            await context.SendSignalAsync(target, taskId, name, response, throwOnRejection: true, cancellationToken);
    }

    /// <summary>
    /// Delivers <paramref name="signal"/> with <paramref name="value"/> to the root task <paramref name="rootId"/> on
    /// <paramref name="target"/>.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="target">The grain which owns the task.</param>
    /// <param name="rootId">The root ID of a task which a client, a grain, or a durable execution started.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons the send.</param>
    /// <returns>A task which completes as <see cref="SignalDurableTaskAsync{T}(IAddressable, TaskId, DurableSignal{T}, T, CancellationToken)"/> describes.</returns>
    /// <remarks>The send behaves as <see cref="SignalDurableTaskAsync{T}(IAddressable, TaskId, DurableSignal{T}, T, CancellationToken)"/> for <c>TaskId.CreateRoot(rootId)</c>.</remarks>
    public static ValueTask SignalDurableTaskAsync<T>(
        this IAddressable target,
        string rootId,
        DurableSignal<T> signal,
        T value,
        CancellationToken cancellationToken = default)
        => target.SignalDurableTaskAsync(TaskId.CreateRoot(rootId), signal, value, cancellationToken);

    /// <summary>
    /// Delivers <paramref name="signal"/> with <paramref name="value"/> to the invoked task <paramref name="taskId"/> on
    /// <paramref name="target"/>, and returns how the task handled it.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="target">The grain which owns the task.</param>
    /// <param name="taskId">The identifier of a task which a client or another grain invoked on <paramref name="target"/>.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons the send.</param>
    /// <returns>
    /// <see cref="DurableSignalResult.Accepted"/> or <see cref="DurableSignalResult.Duplicate"/> once the task durably
    /// holds the value, or the reason the task rejected it, which changes nothing.
    /// </returns>
    /// <remarks>
    /// The send is a call to <paramref name="target"/>, for clients and ordinary grain methods; it behaves as
    /// <see cref="SignalDurableTaskAsync{T}(IAddressable, TaskId, DurableSignal{T}, T, CancellationToken)"/>, which throws
    /// for the rejections this method returns. A durable execution sends signals to other grains with
    /// <see cref="SignalDurableTaskAsync{T}(IAddressable, TaskId, DurableSignal{T}, T, CancellationToken)"/>, which
    /// records the send for replay, and signals its own grain's tasks with
    /// <see cref="IDurableTaskManager.TrySignalAsync{T}(TaskId, DurableSignal{T}, T, CancellationToken)"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> or <paramref name="signal"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">The caller is a durable execution.</exception>
    public static ValueTask<DurableSignalResult> TrySignalDurableTaskAsync<T>(
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

        if (DurableTaskContext.TryGetCurrent(out _))
        {
            throw new InvalidOperationException(
                "A durable execution sends signals to other grains with SignalDurableTaskAsync, which records the send for replay.");
        }

        return target.AsReference<IDurableTaskGrainExtension>()
            .TrySignalAsync(taskId, signal.Name, DurableTaskResponse.FromResult(value), cancellationToken);
    }

    /// <summary>
    /// Delivers <paramref name="signal"/> with <paramref name="value"/> to the root task <paramref name="rootId"/> on
    /// <paramref name="target"/>, and returns how the task handled it.
    /// </summary>
    /// <typeparam name="T">The signal's value type.</typeparam>
    /// <param name="target">The grain which owns the task.</param>
    /// <param name="rootId">The root ID of a task which a client, a grain, or a durable execution started.</param>
    /// <param name="signal">The signal descriptor which the task's waits share.</param>
    /// <param name="value">The signal's value.</param>
    /// <param name="cancellationToken">A token which abandons the send.</param>
    /// <returns>The task's handling of the signal, as <see cref="TrySignalDurableTaskAsync{T}(IAddressable, TaskId, DurableSignal{T}, T, CancellationToken)"/> returns.</returns>
    /// <remarks>The send behaves as <see cref="TrySignalDurableTaskAsync{T}(IAddressable, TaskId, DurableSignal{T}, T, CancellationToken)"/> for <c>TaskId.CreateRoot(rootId)</c>.</remarks>
    public static ValueTask<DurableSignalResult> TrySignalDurableTaskAsync<T>(
        this IAddressable target,
        string rootId,
        DurableSignal<T> signal,
        T value,
        CancellationToken cancellationToken = default)
        => target.TrySignalDurableTaskAsync(TaskId.CreateRoot(rootId), signal, value, cancellationToken);

    /// <summary>
    /// Gets a reference to the existing root durable task <paramref name="rootId"/> on <paramref name="target"/>, without
    /// scheduling it.
    /// </summary>
    /// <typeparam name="TResult">The durable task result type.</typeparam>
    /// <param name="target">The grain which owns the durable task, which can be the calling grain.</param>
    /// <param name="rootId">The root ID of a task which a client, a grain, or a durable execution started.</param>
    /// <returns>A reference which behaves as <see cref="GetDurableTask{TResult}(IAddressable, TaskId)"/> describes.</returns>
    /// <remarks>The reference addresses <c>TaskId.CreateRoot(rootId)</c>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootId"/> is empty or whitespace.</exception>
    public static DurableTaskReference<TResult> GetDurableTask<TResult>(this IAddressable target, string rootId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        return new(target, TaskId.CreateRoot(rootId));
    }

    /// <summary>
    /// Gets a reference to the existing durable task <paramref name="taskId"/> on <paramref name="target"/>, without
    /// scheduling it.
    /// </summary>
    /// <typeparam name="TResult">The durable task result type.</typeparam>
    /// <param name="target">The grain which owns the durable task, which can be the calling grain.</param>
    /// <param name="taskId">The identifier of a task which a client, a grain, or a durable execution started.</param>
    /// <returns>
    /// A reference which a durable execution awaits, schedules, or passes to combinators to subscribe to the task's
    /// result, and which other code awaits, polls, cancels, or aborts.
    /// </returns>
    /// <remarks>
    /// <para>
    /// In a durable execution, awaiting the reference, scheduling it, or passing it to a combinator such as
    /// <c>WhenAny</c>, <c>Race</c>, <c>WaitAsync</c>, or <c>WaitUntilAsync</c> subscribes a child of the execution to the
    /// task, under the identifier which that operation assigns. The child completes with the task's outcome: its
    /// result, failure, or cancellation. The child's record and its subscription commit in one journal write, so a
    /// replay which reaches the record doesn't subscribe again and observes the recorded outcome.
    /// </para>
    /// <para>
    /// <paramref name="target"/> adds the child to the task's completion destinations, beside the task's invoker, sends
    /// the task's result through its outbox, as it does to the invoker, and retains the task until that completion
    /// retires. A subscription to a terminal task completes promptly, and a task which <paramref name="target"/>
    /// doesn't retain as an invoked task, such as one whose retention ended, completes the child with
    /// <see cref="DurableTaskNotFoundException"/> promptly. A task identifier which escapes the code which started the
    /// task refers to a task which its grain holds: <c>ScheduleAsync</c> returns once the task's grain has accepted the
    /// task, and an execution which starts a detached task observes it through the start itself.
    /// </para>
    /// <para>
    /// Canceling the child, for example when a <c>WaitAsync</c> deadline passes or a <c>Race</c> input loses, ends it as
    /// canceled and ends its subscription; the task continues. Durable cancellation of the execution reaches the child
    /// like any child, unless the execution started it shielded.
    /// </para>
    /// <para>
    /// Outside a durable execution, such as in a client or an ordinary grain method, awaiting the reference waits for the
    /// task's result, and its polling members call <paramref name="target"/>. A durable execution doesn't record those
    /// calls.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is empty.</exception>
    public static DurableTaskReference<TResult> GetDurableTask<TResult>(this IAddressable target, TaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (taskId.IsDefault)
        {
            throw new ArgumentException("A durable task reference requires a task identifier.", nameof(taskId));
        }

        return new(target, taskId);
    }
}

internal sealed class AttachedScheduledTask<TResult>(
    TaskId taskId,
    GrainId target,
    IDurableTaskServer server) : ScheduledTask<TResult>
{
    private readonly GrainScheduledTaskHandle _handle =
        new(taskId, target, request: null, server, lastResponse: null);

    public override TaskId Id => taskId;

    public override TFeature? GetFeature<TFeature>()
        where TFeature : class => _handle.GetFeature<TFeature>();

    public override ValueTask CancelAsync(CancellationToken cancellationToken = default) =>
        _handle.CancelAsync(cancellationToken);

    protected override ValueTask CancelAsyncCore(string? reason, CancellationToken cancellationToken) =>
        _handle.CancelAsync(reason, cancellationToken);

    protected override ValueTask AbortAsyncCore(string reason, CancellationToken cancellationToken) =>
        _handle.AbortAsync(reason, cancellationToken);

    protected override ValueTask<DurableTaskResponse> PollAsyncCore(
        PollingOptions options,
        CancellationToken cancellationToken) =>
        _handle.PollAsync(options, cancellationToken);

    protected override ValueTask<DurableTaskResponse> WaitAsyncCore(CancellationToken cancellationToken) =>
        _handle.WaitAsync(cancellationToken);
}

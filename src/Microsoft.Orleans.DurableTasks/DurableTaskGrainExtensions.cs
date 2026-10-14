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
    /// <param name="rootId">The root ID under which a client or another grain invoked or detached the task.</param>
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
    /// <param name="rootId">The root ID under which a client or another grain invoked or detached the task.</param>
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
    /// Gets a typed handle which observes an existing root durable task without scheduling it.
    /// </summary>
    /// <typeparam name="TResult">The durable task result type.</typeparam>
    /// <param name="target">The grain which owns the durable task.</param>
    /// <param name="rootId">The stable root task identifier.</param>
    /// <returns>A handle which can poll, wait for, cancel, or abort the existing durable task.</returns>
    /// <remarks>
    /// The handle polls the target with grain calls, which a durable execution doesn't record. A durable execution
    /// awaits another task's result with <see cref="ObserveDurableTask{TResult}(IAddressable, string)"/>.
    /// </remarks>
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

    /// <summary>
    /// Creates a durable task which completes with the terminal result of the existing invoked task
    /// <paramref name="taskId"/> on <paramref name="target"/>, for a durable execution which didn't invoke it.
    /// </summary>
    /// <typeparam name="TResult">The observed task's result type.</typeparam>
    /// <param name="target">The grain which owns the observed task, which can be the calling grain.</param>
    /// <param name="taskId">
    /// The identifier of a task which a client, another grain, or <c>DetachAsync</c> invoked on <paramref name="target"/>.
    /// </param>
    /// <returns>
    /// An observation which the calling durable execution awaits, schedules, or passes to combinators such as
    /// <see cref="DurableTask.WhenAny{TResult}(IReadOnlyList{DurableTask{TResult}})"/>, <c>Race</c>, and <c>WaitAsync</c>.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The observation is a child of the calling execution, recorded in its tree under the identifier which awaiting or
    /// scheduling it assigns, like a call to another grain. Scheduling it commits the record and a subscription in one
    /// journal write; a replay which reaches the record doesn't subscribe again and observes the recorded outcome.
    /// </para>
    /// <para>
    /// The subscription travels through the outbox to <paramref name="target"/>, which sends the observed task's terminal
    /// result through its outbox, as it does to the task's caller, and retains that result until the completion retires.
    /// An observed task which is already terminal completes the observation promptly. A subscription can arrive before
    /// the invocation of the task which it observes, for example when one write of another grain starts both the task and
    /// the observing execution, so <paramref name="target"/> holds a subscription to a task which it doesn't store until
    /// that invocation arrives, or until Messaging's <c>OutboxRetryTimeout</c> has elapsed, after which it can no longer
    /// arrive. A task which <paramref name="target"/> doesn't retain as an invoked task then, such as one whose retention
    /// ended, completes the observation with <see cref="DurableTaskNotFoundException"/>; so does a stored task which isn't
    /// an invoked task, at once. The observation's result
    /// is the observed task's outcome: its result, failure, or cancellation.
    /// </para>
    /// <para>
    /// Canceling the observation, for example when a <c>WaitAsync</c> deadline passes or a <c>Race</c> input loses, ends it
    /// as canceled and ends its subscription; the observed task continues. Durable cancellation of the observing execution
    /// reaches the observation like any child, unless the execution scheduled it in a cancellation suppression scope.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="taskId"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The caller isn't a durable execution of a grain. Clients and ordinary grain methods poll with
    /// <see cref="GetDurableTask{TResult}(IAddressable, string)"/>.
    /// </exception>
    public static DurableTask<TResult> ObserveDurableTask<TResult>(this IAddressable target, TaskId taskId)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (taskId.IsDefault)
        {
            throw new ArgumentException("An observation requires a task identifier.", nameof(taskId));
        }

        if (!DurableTaskContext.TryGetCurrent(out var current) || current is not GrainDurableTaskContext)
        {
            throw new InvalidOperationException(
                "A durable task observation is created in a durable execution of a grain, which records it. Clients and ordinary grain methods poll existing tasks with GetDurableTask.");
        }

        return new DurableTaskObservation<TResult>(target.GetGrainId(), taskId);
    }

    /// <summary>
    /// Creates a durable task which completes with the terminal result of the existing root task <paramref name="rootId"/>
    /// on <paramref name="target"/>, for a durable execution which didn't invoke it.
    /// </summary>
    /// <typeparam name="TResult">The observed task's result type.</typeparam>
    /// <param name="target">The grain which owns the observed task, which can be the calling grain.</param>
    /// <param name="rootId">The root ID under which a client, another grain, or <c>DetachAsync</c> invoked the task.</param>
    /// <returns>An observation, as <see cref="ObserveDurableTask{TResult}(IAddressable, TaskId)"/> returns.</returns>
    /// <remarks>The observation behaves as <see cref="ObserveDurableTask{TResult}(IAddressable, TaskId)"/> for <c>TaskId.CreateRoot(rootId)</c>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootId"/> is empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The caller isn't a durable execution of a grain.</exception>
    public static DurableTask<TResult> ObserveDurableTask<TResult>(this IAddressable target, string rootId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootId);
        return target.ObserveDurableTask<TResult>(TaskId.CreateRoot(rootId));
    }
}

/// <summary>An observation of an existing invoked task, which the durable task runtime schedules as a child.</summary>
internal interface IDurableTaskObservation
{
    /// <summary>Gets the grain which owns the observed task.</summary>
    GrainId Target { get; }

    /// <summary>Gets the observed task.</summary>
    TaskId TaskId { get; }
}

internal sealed class DurableTaskObservation<TResult>(GrainId target, TaskId taskId) : DurableTask<TResult>, IDurableTaskObservation
{
    public GrainId Target => target;

    public TaskId TaskId => taskId;

    protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context) =>
        throw new InvalidOperationException("A durable task observation is scheduled by the durable task runtime of a grain.");
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

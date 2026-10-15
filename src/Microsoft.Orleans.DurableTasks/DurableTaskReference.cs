using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Orleans.DurableTasks.Protocol;
using Orleans.Runtime;

namespace Orleans.DurableTasks;

/// <summary>
/// A reference to an existing durable task of a grain, which
/// <see cref="DurableTaskGrainExtensions.GetDurableTask{TResult}(IAddressable, TaskId)"/> returns.
/// </summary>
/// <typeparam name="TResult">The task's result type.</typeparam>
/// <remarks>
/// <para>
/// In a durable execution, the reference is a durable task definition: awaiting it, scheduling it, or passing it to a
/// combinator subscribes a child of the execution to the task's result, which the execution records for replay.
/// </para>
/// <para>
/// Outside a durable execution, awaiting the reference waits for the task's result, and its polling members, such as
/// <see cref="GetResponseAsync(CancellationToken)"/>, <see cref="WaitAsync(CancellationToken)"/>, and
/// <see cref="GetStatusAsync"/>, and its control members,
/// <see cref="CancelAsync(string, CancellationToken)"/> and <see cref="AbortAsync"/>, call the task's grain. A durable
/// execution doesn't record those calls.
/// </para>
/// <para>
/// The reference is awaitable through <see cref="DurableTaskReferenceExtensions.GetAwaiter{TResult}"/>, an extension
/// method, so frameworks which inspect a return type's instance members, such as ASP.NET Core Minimal APIs, treat the
/// reference as a value.
/// </para>
/// </remarks>
public sealed class DurableTaskReference<TResult> : DurableTask<TResult>, IDurableTaskSubscription
{
    private readonly IAddressable _target;
    private ScheduledTask<TResult>? _handle;

    internal DurableTaskReference(IAddressable target, TaskId taskId)
    {
        _target = target;
        Target = target.GetGrainId();
        Id = taskId;
    }

    /// <summary>Gets the identifier of the referenced task.</summary>
    public TaskId Id { get; }

    /// <summary>Gets the grain which owns the referenced task.</summary>
    public GrainId Target { get; }

    TaskId IDurableTaskSubscription.TaskId => Id;

    private ScheduledTask<TResult> Handle =>
        _handle ??= new AttachedScheduledTask<TResult>(Id, _target.AsReference<IDurableTaskGrainExtension>());

    /// <inheritdoc cref="DurableTaskReferenceExtensions.GetAwaiter{TResult}"/>
    internal DurableTaskReferenceAwaiter<TResult> CreateAwaiter() =>
        DurableTaskContext.TryGetCurrent(out _)
            ? new(DurableTaskExtensions.GetAwaiter(this))
            : new(Handle.GetAwaiter());

    /// <summary>Waits for the task's terminal response.</summary>
    /// <param name="cancellationToken">A token which abandons the wait. It doesn't cancel the task.</param>
    /// <exception cref="DurableTaskNotFoundException">The grain retains no task with this identifier.</exception>
    public Task<DurableTaskResponse> GetResponseAsync(CancellationToken cancellationToken = default) =>
        Handle.GetResponseAsync(cancellationToken);

    /// <summary>Waits for the task's successful result.</summary>
    /// <param name="cancellationToken">A token which abandons the wait. It doesn't cancel the task.</param>
    /// <returns>An awaitable which returns the result, or throws the task's failure or cancellation.</returns>
    /// <remarks>
    /// The wait calls the task's grain. In a durable execution, await the reference itself, or pass it to
    /// <c>WaitAsync(TimeSpan)</c>, so the execution records the wait.
    /// </remarks>
    public ConfiguredScheduledTaskAwaitable<TResult> WaitAsync(CancellationToken cancellationToken = default) =>
        Handle.WaitAsync(cancellationToken);

    /// <summary>Polls the task and returns whether it is terminal.</summary>
    /// <param name="options">The options of the poll.</param>
    /// <param name="cancellationToken">A token which abandons the poll.</param>
    /// <exception cref="DurableTaskNotFoundException">The grain retains no task with this identifier.</exception>
    public Task<bool> IsCompletedAsync(PollingOptions options = default, CancellationToken cancellationToken = default) =>
        Handle.IsCompletedAsync(options, cancellationToken);

    /// <summary>Waits for the task's terminal response, polling with <paramref name="options"/>.</summary>
    /// <param name="options">The options of each poll.</param>
    /// <param name="cancellationToken">A token which abandons the wait. It doesn't cancel the task.</param>
    /// <exception cref="DurableTaskNotFoundException">The grain retains no task with this identifier.</exception>
    public Task<DurableTaskResponse> GetResponseAsync(PollingOptions options, CancellationToken cancellationToken = default) =>
        Handle.GetResponseAsync(options, cancellationToken);

    /// <summary>Polls the task and returns its current status.</summary>
    /// <param name="options">The options of the poll.</param>
    /// <param name="cancellationToken">A token which abandons the poll.</param>
    /// <exception cref="DurableTaskNotFoundException">The grain retains no task with this identifier.</exception>
    public Task<DurableTaskStatus> GetStatusAsync(PollingOptions options = default, CancellationToken cancellationToken = default) =>
        Handle.GetStatusAsync(options, cancellationToken);

    /// <summary>Requests durable cancellation of the task.</summary>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    public ValueTask CancelAsync(CancellationToken cancellationToken = default) => Handle.CancelAsync(cancellationToken);

    /// <summary>Requests durable cancellation of the task carrying <paramref name="reason"/>.</summary>
    /// <param name="reason">
    /// The reason which the task's <see cref="DurableTaskContext.CancellationReason"/> reports, or <see langword="null"/>
    /// for none. The first request determines the reason.
    /// </param>
    /// <param name="cancellationToken">A token which abandons waiting for the request to commit.</param>
    public ValueTask CancelAsync(string? reason, CancellationToken cancellationToken = default) =>
        Handle.CancelAsync(reason, cancellationToken);

    /// <summary>
    /// Administratively aborts the task, which terminalizes it and its execution tree as failed with
    /// <see cref="DurableTaskAbortedException"/> without running further task code.
    /// </summary>
    /// <param name="reason">The reason for the abort, recorded as the exception message.</param>
    /// <param name="cancellationToken">A token which abandons waiting for the abort to commit.</param>
    public ValueTask AbortAsync(string reason, CancellationToken cancellationToken = default) =>
        Handle.AbortAsync(reason, cancellationToken);

    /// <inheritdoc/>
    protected override ValueTask<DurableTaskResponse> RunAsync(DurableTaskContext context) =>
        throw new InvalidOperationException(
            "A durable task reference is scheduled by the durable task runtime of a grain, which subscribes to the referenced task.");
}

/// <summary>
/// Awaits a <see cref="DurableTaskReference{TResult}"/>: as a subscribed child in a durable execution, and otherwise by
/// waiting for the task's result.
/// </summary>
/// <typeparam name="TResult">The task's result type.</typeparam>
[SuppressMessage("Performance", "CA1815:Override equals and operator equals on value types", Justification = "Awaiters are compiler protocol values whose identity and equality are not part of their contract.")]
public readonly struct DurableTaskReferenceAwaiter<TResult> : ICriticalNotifyCompletion
{
    private readonly DurableTaskAwaiter<TResult> _subscription;
    private readonly ScheduledTaskAwaiter<TResult> _wait;
    private readonly bool _subscribes;

    internal DurableTaskReferenceAwaiter(DurableTaskAwaiter<TResult> subscription)
    {
        _subscription = subscription;
        _wait = default;
        _subscribes = true;
    }

    internal DurableTaskReferenceAwaiter(ScheduledTaskAwaiter<TResult> wait)
    {
        _subscription = default;
        _wait = wait;
        _subscribes = false;
    }

    /// <inheritdoc cref="DurableTaskAwaiter{TResult}.IsCompleted"/>
    public bool IsCompleted => _subscribes ? _subscription.IsCompleted : _wait.IsCompleted;

    /// <inheritdoc/>
    public void OnCompleted(Action continuation)
    {
        if (_subscribes)
        {
            _subscription.OnCompleted(continuation);
        }
        else
        {
            _wait.OnCompleted(continuation);
        }
    }

    /// <inheritdoc/>
    public void UnsafeOnCompleted(Action continuation)
    {
        if (_subscribes)
        {
            _subscription.UnsafeOnCompleted(continuation);
        }
        else
        {
            _wait.UnsafeOnCompleted(continuation);
        }
    }

    /// <summary>Returns the task's successful result, or throws its failure or cancellation.</summary>
    public TResult GetResult() => _subscribes ? _subscription.GetResult() : _wait.GetResult();
}

/// <summary>Awaits references to existing durable tasks.</summary>
public static class DurableTaskReferenceExtensions
{
    /// <summary>
    /// Gets an awaiter which, in a durable execution, subscribes a child of the execution to the task's result, and
    /// otherwise waits for the task's result.
    /// </summary>
    /// <typeparam name="TResult">The task's result type.</typeparam>
    /// <param name="reference">The reference to the task.</param>
    public static DurableTaskReferenceAwaiter<TResult> GetAwaiter<TResult>(this DurableTaskReference<TResult> reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return reference.CreateAwaiter();
    }
}

/// <summary>A reference to an existing task, which the durable task runtime schedules as a subscription child.</summary>
internal interface IDurableTaskSubscription
{
    /// <summary>Gets the grain which owns the referenced task.</summary>
    GrainId Target { get; }

    /// <summary>Gets the referenced task.</summary>
    TaskId TaskId { get; }
}

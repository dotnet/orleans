namespace Orleans.DurableTasks;

/// <summary>Provides awaiting, identification, scheduling, timeout, and combinator operations for durable task definitions and handles.</summary>
public static partial class DurableTaskExtensions
{
    /// <summary>Gets an awaiter for a durable task in the current execution context.</summary>
    public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => new ConfiguredDurableTask(task).GetAwaiter();

    /// <summary>Gets an awaiter for a durable task in the current execution context.</summary>
    public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).GetAwaiter();

    /// <summary>Returns a configuration which names the task.</summary>
    /// <param name="task">The definition to start.</param>
    /// <param name="name">The task's name.</param>
    /// <remarks>
    /// <para>
    /// Within a durable execution, the name identifies an attached child among the execution's operations, and a replay
    /// reaches the same child. A name repeated in one run identifies its later occurrences by their order, such as
    /// <c>reserve$2</c>, and names don't contain <c>$</c>. A detached task uses the name verbatim as its root identifier,
    /// so other parties can address it, for example with <c>GetDurableTask</c>.
    /// </para>
    /// <para>
    /// Outside a durable execution, every task is a root task, and the name is its root identifier. The name makes a start
    /// safe to retry: a caller which retries a start after an ambiguous failure, such as a timeout, reaches the same task.
    /// A start without a name receives a new random root identifier, which its handle's <see cref="ScheduledTask.Id"/>
    /// reports, so it behaves like an ordinary call.
    /// </para>
    /// </remarks>
    public static ConfiguredDurableTask WithId(this DurableTask task, string name) => new ConfiguredDurableTask(task).WithId(name);

    /// <inheritdoc cref="WithId(DurableTask, string)"/>
    public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, string name) => new ConfiguredDurableTask<TResult>(task).WithId(name);

    /// <summary>Returns a configuration which starts the task as an independent root task.</summary>
    /// <param name="task">The definition to start, such as a durable grain call.</param>
    /// <remarks>
    /// <para>
    /// Within a durable execution, a detached task is a root of its own: the execution doesn't own it, can complete while
    /// it runs, and its cancellation, abort, and deletion don't reach it. Awaiting the detached task, or the handle which
    /// <see cref="ConfiguredDurableTask.ScheduleAsync"/> or <see cref="ConfiguredDurableTask.Post"/> returns, observes
    /// its result: the start names the execution as a subscriber, and canceling the wait ends only that subscription.
    /// </para>
    /// <para>
    /// The task's root identifier is the name which <see cref="WithId(DurableTask, string)"/> assigns, verbatim, or
    /// <c>{executionId}:{segment}</c>, where <c>{segment}</c> is the identifier which the execution generates for its next
    /// unnamed operation, so a replay derives the same identifier. Starting a detached task again under the same root
    /// identifier, including during a replay, reaches the same task.
    /// </para>
    /// <para>Outside a durable execution, every task is already a root task, so the configuration is unchanged.</para>
    /// </remarks>
    public static ConfiguredDurableTask Detached(this DurableTask task) => new ConfiguredDurableTask(task).Detached();

    /// <inheritdoc cref="Detached(DurableTask)"/>
    public static ConfiguredDurableTask<TResult> Detached<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).Detached();

    /// <summary>
    /// Returns a configuration which starts the task as an attached child, or runs it as part of the execution, out of
    /// reach of the starting execution's durable cancellation.
    /// </summary>
    /// <param name="task">The definition to start.</param>
    /// <remarks>
    /// <para>
    /// The execution still owns a shielded child and waits for it: the child runs to completion even when the execution's
    /// cancellation was requested before the child started, which suits compensation that a canceled workflow runs. The
    /// execution's own <see cref="DurableTaskContext.CancellationToken"/> and
    /// <see cref="DurableTaskContext.IsTaskCancellationRequested"/> still report its cancellation. An administrative abort
    /// and deleting the execution's tree still reach the child, and canceling the child through its own handle still
    /// cancels it and its children.
    /// </para>
    /// <para>
    /// A shielded child's own children are reached only by the child's cancellation, so the whole subtree, such as a
    /// <see cref="DurableTask.Run(Func{CancellationToken, Task})"/> step and the work it starts, runs to completion. A
    /// shielded combinator, such as <see cref="DurableTask.WhenAll(IReadOnlyList{DurableTask})"/>, runs as part of the
    /// execution and shields each input which it starts.
    /// </para>
    /// <para>
    /// Every awaited durable operation honors the shield in the same way, whether it runs as a child or as part of the
    /// execution: a signal wait, a delay, a subscription to an existing task, a call to another grain, and combinators
    /// such as <c>Race</c>, <c>WhenAny</c>, <c>WaitAsync</c>, and <see cref="DurableSignal{T}.TryWaitAsync"/> complete
    /// after the execution's cancellation, and an administrative abort ends them. A detached task is independent of the
    /// execution, and a shield keeps the execution's cancellation from ending the execution's subscription to its result.
    /// </para>
    /// </remarks>
    public static ConfiguredDurableTask Shielded(this DurableTask task) => new ConfiguredDurableTask(task).Shielded();

    /// <inheritdoc cref="Shielded(DurableTask)"/>
    public static ConfiguredDurableTask<TResult> Shielded<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).Shielded();

    /// <inheritdoc cref="ConfiguredDurableTask.ScheduleAsync(CancellationToken)"/>
    public static Task<ScheduledTask> ScheduleAsync(this DurableTask task, CancellationToken cancellationToken = default)
        => new ConfiguredDurableTask(task).ScheduleAsync(cancellationToken);

    /// <inheritdoc cref="ConfiguredDurableTask.ScheduleAsync(CancellationToken)"/>
    public static Task<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task, CancellationToken cancellationToken = default)
        => new ConfiguredDurableTask<TResult>(task).ScheduleAsync(cancellationToken);

    /// <inheritdoc cref="ConfiguredDurableTask.Post"/>
    public static ScheduledTask Post(this DurableTask task) => new ConfiguredDurableTask(task).Post();

    /// <inheritdoc cref="ConfiguredDurableTask.Post"/>
    public static ScheduledTask<TResult> Post<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).Post();

    /// <summary>
    /// Creates a durable task which runs <paramref name="task"/> and requests its durable cancellation once
    /// <paramref name="timeout"/> elapses.
    /// </summary>
    /// <param name="task">The task to run.</param>
    /// <param name="timeout">The time to wait before requesting cancellation, which is zero or positive.</param>
    /// <returns>
    /// A combinator which, awaited within a durable execution, runs as part of that execution and completes once
    /// <paramref name="task"/> is terminal.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The combinator races <paramref name="task"/> with <see cref="DurableTask.Delay(TimeSpan)"/>, as
    /// <see cref="DurableTask.Race(IReadOnlyList{DurableTask})"/> does, and the host selects the same winner on replay. The
    /// delay is measured using host logical time from its first run, and a replay keeps its recorded due time, so the
    /// timeout counts from the first time the combinator runs. The task runs as the child <c>0</c> and the delay as the child
    /// <c>1</c> under the combinator's operation identifier, which also identifies a recorded selection: the segment
    /// assigned with <c>WithId</c>, or a segment generated in execution order.
    /// </para>
    /// <para>
    /// When the task completes first, the combinator cancels the delay and completes with the task's outcome: its result,
    /// failure, or cancellation. When the timeout elapses first, the combinator requests cancellation of the task carrying
    /// the reason <c>The operation timed out after {timeout}.</c>, which the task's
    /// <see cref="DurableTaskContext.TaskCancellationReason"/> reports when the request is the task's first, and waits until the
    /// task is terminal: nothing which the combinator started outlives it. A task which then ends canceled fails the
    /// combinator with a <see cref="TimeoutException"/> whose inner exception is the task's
    /// <see cref="OperationCanceledException"/>, and any other outcome, such as a result which the task returns after
    /// observing the request, is the combinator's outcome. A delay which ends without elapsing, for example because durable
    /// cancellation of the awaiting execution reached it, also requests cancellation of the task, and the combinator then
    /// fails with the delay's exception once the task is terminal.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="timeout"/> is negative, including <see cref="Timeout.InfiniteTimeSpan"/>; await the task itself to
    /// wait without a timeout.
    /// </exception>
    public static DurableTask WaitAsync(this DurableTask task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        return DurableTask.WithTimeout(task, DurableTask.Delay(timeout), ScheduledTask.GetTimeoutReason(timeout));
    }

    /// <summary>
    /// Creates a durable task which runs <paramref name="task"/>, returns its result, and requests its durable cancellation
    /// once <paramref name="timeout"/> elapses.
    /// </summary>
    /// <inheritdoc cref="WaitAsync(DurableTask, TimeSpan)"/>
    public static DurableTask<TResult> WaitAsync<TResult>(this DurableTask<TResult> task, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        return DurableTask.WithTimeout(task, DurableTask.Delay(timeout), ScheduledTask.GetTimeoutReason(timeout));
    }

    /// <summary>
    /// Creates a durable task which runs <paramref name="task"/> and requests its durable cancellation once
    /// <paramref name="dueTime"/> passes.
    /// </summary>
    /// <param name="task">The task to run.</param>
    /// <param name="dueTime">
    /// The time, measured using host logical time, at which to request cancellation. A time which has passed times out
    /// promptly.
    /// </param>
    /// <returns>
    /// A combinator which, awaited within a durable execution, runs as part of that execution and completes once
    /// <paramref name="task"/> is terminal.
    /// </returns>
    /// <remarks>
    /// The combinator behaves as <see cref="WaitAsync(DurableTask, TimeSpan)"/>, racing <paramref name="task"/> with
    /// <see cref="DurableTask.DelayUntil(DateTimeOffset)"/>, and requests cancellation with the reason
    /// <c>The operation timed out because its deadline {dueTime} passed.</c>, which formats <paramref name="dueTime"/> with
    /// the round-trip format, <c>O</c>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> is <see langword="null"/>.</exception>
    public static DurableTask WaitUntilAsync(this DurableTask task, DateTimeOffset dueTime)
    {
        ArgumentNullException.ThrowIfNull(task);
        return DurableTask.WithTimeout(task, DurableTask.DelayUntil(dueTime), ScheduledTask.GetDeadlineReason(dueTime));
    }

    /// <summary>
    /// Creates a durable task which runs <paramref name="task"/>, returns its result, and requests its durable cancellation
    /// once <paramref name="dueTime"/> passes.
    /// </summary>
    /// <inheritdoc cref="WaitUntilAsync(DurableTask, DateTimeOffset)"/>
    public static DurableTask<TResult> WaitUntilAsync<TResult>(this DurableTask<TResult> task, DateTimeOffset dueTime)
    {
        ArgumentNullException.ThrowIfNull(task);
        return DurableTask.WithTimeout(task, DurableTask.DelayUntil(dueTime), ScheduledTask.GetDeadlineReason(dueTime));
    }
}

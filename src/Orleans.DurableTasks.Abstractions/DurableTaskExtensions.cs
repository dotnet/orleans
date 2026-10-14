namespace Orleans.DurableTasks;

/// <summary>Provides awaiting, identification, scheduling, and timeout operations for durable task definitions.</summary>
public static class DurableTaskExtensions
{
    /// <summary>Gets an awaiter for a durable task in the current execution context.</summary>
    public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => new ConfiguredDurableTask(task).GetAwaiter();

    /// <summary>Gets an awaiter for a durable task in the current execution context.</summary>
    public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).GetAwaiter();

    /// <summary>
    /// Assigns one stable logical identifier segment. Within a durable execution, a segment repeated in one run
    /// identifies its later occurrences by their order, such as <c>reserve$2</c>, and segments don't contain <c>$</c>.
    /// </summary>
    public static ConfiguredDurableTask WithId(this DurableTask task, string segment) => new ConfiguredDurableTask(task).WithId(segment);

    /// <summary>
    /// Assigns one stable logical identifier segment. Within a durable execution, a segment repeated in one run
    /// identifies its later occurrences by their order, such as <c>reserve$2</c>, and segments don't contain <c>$</c>.
    /// </summary>
    public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, string segment) => new ConfiguredDurableTask<TResult>(task).WithId(segment);

    /// <summary>Schedules a root task using one explicit stable identifier segment.</summary>
    public static Task<ScheduledTask> ScheduleAsync(this DurableTask task, string rootId, CancellationToken cancellationToken = default)
        => new ConfiguredDurableTask(task, rootId).ScheduleAsync(cancellationToken);

    /// <summary>Schedules a root task using one explicit stable identifier segment.</summary>
    public static Task<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task, string rootId, CancellationToken cancellationToken = default)
        => new ConfiguredDurableTask<TResult>(task, rootId).ScheduleAsync(cancellationToken);

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
    /// <see cref="DurableTaskContext.CancellationReason"/> reports when the request is the task's first, and waits until the
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

    /// <summary>
    /// Starts <paramref name="task"/> as a new root task with identifier <paramref name="rootId"/>, detached from its
    /// starter.
    /// </summary>
    /// <param name="task">The definition to start, such as a durable grain call.</param>
    /// <param name="rootId">The stable root identifier of the detached task.</param>
    /// <param name="cancellationToken">A token which abandons scheduling. It doesn't cancel the detached task.</param>
    /// <returns>An operation which completes once the host has durably accepted the detached task.</returns>
    /// <remarks>
    /// <para>
    /// A detached task is a root of its own: durable cancellation of the starter doesn't cancel it, the starter can't
    /// await its result, and the host retains its result for polling by <paramref name="rootId"/> as it does for other
    /// roots without an observing caller. Detaching the same definition under the same identifier again leaves the task
    /// unchanged; another definition under that identifier fails like other identifier reuse.
    /// </para>
    /// <para>
    /// Within a durable execution, the host records the start in the execution under an identifier which it derives from
    /// the detached task's identity and commits both together, so a replay doesn't start the task again. Adding or
    /// removing other operations doesn't change the record which a detach reaches. Outside a durable execution, the
    /// definition must support root scheduling.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="task"/> or <paramref name="rootId"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="rootId"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// Outside a durable execution, <paramref name="task"/> doesn't support root scheduling.
    /// </exception>
    public static ValueTask DetachAsync(this DurableTask task, string rootId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        var taskId = TaskId.CreateRoot(rootId);
        if (DurableTaskContext.Ambient is { } context)
        {
            return context.DetachTaskAsync(taskId, task, cancellationToken);
        }

        return task is ISchedulableTask schedulable
            ? schedulable.DetachAsync(taskId, cancellationToken)
            : throw new InvalidOperationException("The durable task definition does not support root scheduling.");
    }
}

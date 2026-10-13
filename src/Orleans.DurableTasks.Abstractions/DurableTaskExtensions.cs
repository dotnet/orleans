namespace Orleans.DurableTasks;

/// <summary>Provides awaiting, identification, and scheduling operations for durable task definitions.</summary>
public static class DurableTaskExtensions
{
    /// <summary>Gets an awaiter for a durable task in the current execution context.</summary>
    public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => new ConfiguredDurableTask(task).GetAwaiter();

    /// <summary>Gets an awaiter for a durable task in the current execution context.</summary>
    public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).GetAwaiter();

    /// <summary>
    /// Assigns one stable logical identifier segment. Within a durable execution, segments beginning
    /// with <c>$</c> are reserved for generated identifiers.
    /// </summary>
    public static ConfiguredDurableTask WithId(this DurableTask task, string segment) => new ConfiguredDurableTask(task).WithId(segment);

    /// <summary>
    /// Assigns one stable logical identifier segment. Within a durable execution, segments beginning
    /// with <c>$</c> are reserved for generated identifiers.
    /// </summary>
    public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, string segment) => new ConfiguredDurableTask<TResult>(task).WithId(segment);

    /// <summary>Schedules a root task using one explicit stable identifier segment.</summary>
    public static Task<ScheduledTask> ScheduleAsync(this DurableTask task, string rootId, CancellationToken cancellationToken = default)
        => new ConfiguredDurableTask(task, rootId).ScheduleAsync(cancellationToken);

    /// <summary>Schedules a root task using one explicit stable identifier segment.</summary>
    public static Task<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task, string rootId, CancellationToken cancellationToken = default)
        => new ConfiguredDurableTask<TResult>(task, rootId).ScheduleAsync(cancellationToken);

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
    /// Within a durable execution, the host records the start in the execution with a generated child identifier and
    /// commits both together, so a replay doesn't start the task again. Detach calls in one execution, like unnamed
    /// children, begin in deterministic order. Outside a durable execution, the definition must support root scheduling.
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
        if (DurableExecutionContext.Current is { } context)
        {
            return context.DetachTaskAsync(context.CreateOperationId("detach"), taskId, task, cancellationToken);
        }

        return task is ISchedulableTask schedulable
            ? schedulable.DetachAsync(taskId, cancellationToken)
            : throw new InvalidOperationException("The durable task definition does not support root scheduling.");
    }
}

namespace System.Distributed.DurableTasks;

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
        => task.WithId(rootId).ScheduleAsync(cancellationToken);
    /// <summary>Schedules a root task using one explicit stable identifier segment.</summary>
    public static Task<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task, string rootId, CancellationToken cancellationToken = default)
        => task.WithId(rootId).ScheduleAsync(cancellationToken);
}

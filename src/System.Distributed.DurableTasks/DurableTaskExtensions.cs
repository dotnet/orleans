using System.Diagnostics.Contracts;

namespace System.Distributed.DurableTasks;

/// <summary>
/// Extension methods for working with <see cref="DurableTask"/> and <see cref="DurableTask{TResult}"/> instances.
/// </summary>
public static class DurableTaskExtensions
{
    /// <summary>
    /// Gets an awaiter for the durable task. This schedules the task and awaits completion.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>An awaiter for the task.</returns>
    public static DurableTaskAwaiter GetAwaiter(this DurableTask task) => new ConfiguredDurableTask(task).GetAwaiter();

    /// <summary>
    /// Gets an awaiter for the durable task. This schedules the task and awaits completion.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>An awaiter for the task.</returns>
    public static DurableTaskAwaiter<TResult> GetAwaiter<TResult>(this DurableTask<TResult> task) => new ConfiguredDurableTask<TResult>(task).GetAwaiter();

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="taskId">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask WithId(this DurableTask task, string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return new ConfiguredDurableTask(task).WithId(taskId);
    }

    /// <summary>
    /// Returns a configured task with an identifier set.
    /// If the caller is executing in the context of a <see cref="DurableTask"/>, this identifier is relative to the parent task.
    /// If the caller is not executing in the context of a <see cref="DurableTask"/>, this identifier is absolute.
    /// </summary>
    /// <param name="taskId">The identifier.</param>
    /// <returns>This instance.</returns>
    [Pure]
    public static ConfiguredDurableTask<TResult> WithId<TResult>(this DurableTask<TResult> task, string taskId)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return new ConfiguredDurableTask<TResult>(task).WithId(taskId);
    }

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="task">The task.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static Task<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task, CancellationToken cancellationToken = default) => new ConfiguredDurableTask<TResult>(task).ScheduleAsync(cancellationToken);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask{TResult}"/> as a workflow using the provided identifier.
    /// </summary>
    /// <typeparam name="TResult">The task result type.</typeparam>
    /// <param name="task">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static Task<ScheduledTask<TResult>> ScheduleAsync<TResult>(this DurableTask<TResult> task, string taskId, CancellationToken cancellationToken = default) => new ConfiguredDurableTask<TResult>(task).WithId(taskId).ScheduleAsync(cancellationToken);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static Task<ScheduledTask> ScheduleAsync(this DurableTask task, CancellationToken cancellationToken = default) => new ConfiguredDurableTask(task).ScheduleAsync(cancellationToken);

    /// <summary>
    /// Schedules the provided <see cref="DurableTask"/> as a workflow using the provided identifier.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle for the scheduled task.</returns>
    public static Task<ScheduledTask> ScheduleAsync(this DurableTask task, string taskId, CancellationToken cancellationToken = default) => new ConfiguredDurableTask(task).WithId(taskId).ScheduleAsync(cancellationToken);
}

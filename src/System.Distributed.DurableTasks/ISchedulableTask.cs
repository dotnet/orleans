namespace System.Distributed.DurableTasks;

/// <summary>
/// Interface implemented by <see cref="DurableTask"/> and <see cref="DurableTask{TResult}"/> implementations allowing them to be scheduled.
/// </summary>
public interface ISchedulableTask
{
    /// <summary>
    /// Schedules the task, returning a handle to the scheduled task.
    /// </summary>
    /// <returns>A handle representing the scheduled task.</returns>
    ValueTask<DurableTaskResponse> ScheduleAsync(TaskId taskId, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a handle to a scheduled task.
    /// </summary>
    /// <param name="taskId">The task identifier.</param>
    /// <returns>A handle representing the scheduled task.</returns>
    IScheduledTaskHandle GetHandle(TaskId taskId);
}

public interface IScheduledTaskHandle
{
    /// <summary>
    /// Gets the identifier of the task.
    /// </summary>
    TaskId TaskId { get; }

    /// <summary>
    /// Waits until the task has completed, returning the response.
    /// </summary>
    /// <returns>The task result.</returns>
    ValueTask<DurableTaskResponse> WaitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Polls the task to determine whether it has completed, returning the result if it has completed, or a non-final result (<see cref="DurableTaskResponse.IsCompleted"/> returns <see langword="false"/>) if it has not.
    /// </summary>
    /// <returns>The current task result.</returns>
    ValueTask<DurableTaskResponse> PollAsync(PollingOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Cancels a task.
    /// </summary>
    /// <returns>A task which completes when cancellation has been acknowledged.</returns>
    ValueTask CancelAsync(CancellationToken cancellationToken);
}

public readonly struct PollingOptions()
{
    public TimeSpan PollTimeout { get; init; } = TimeSpan.FromSeconds(5);
}

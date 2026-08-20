using System;
using System.Diagnostics.CodeAnalysis;

namespace Orleans.DurableJobs;

/// <summary>
/// Represents the result of a durable job execution.
/// </summary>
[GenerateSerializer]
public sealed class DurableJobRunResult
{
    /// <summary>
    /// Gets a value indicating whether the job execution failed.
    /// </summary>
    [MemberNotNullWhen(true, nameof(Exception))]
    public bool IsFailed => Status == DurableJobRunStatus.Failed;

    /// <summary>
    /// Gets a value indicating whether the job is still running and should be polled after a delay.
    /// </summary>
    [MemberNotNullWhen(true, nameof(PollAfterDelay))]
    public bool IsRunning => Status == DurableJobRunStatus.Running;

    /// <summary>
    /// Gets a value indicating whether the successfully handled job requested durable rescheduling.
    /// </summary>
    [MemberNotNullWhen(true, nameof(RescheduleTime))]
    public bool IsRescheduleRequested => Status == DurableJobRunStatus.RescheduleRequested && RescheduleTime is not null;

    private DurableJobRunResult(
        DurableJobRunStatus status,
        TimeSpan? pollAfterDelay,
        Exception? exception,
        DateTimeOffset? rescheduleTime)
    {
        Status = status;
        PollAfterDelay = pollAfterDelay;
        Exception = exception;
        RescheduleTime = rescheduleTime;
    }

    /// <summary>
    /// Gets the status of the job execution.
    /// </summary>
    [Id(0)]
    public DurableJobRunStatus Status { get; }

    /// <summary>
    /// Gets the delay before the executor polls the ongoing run when <see cref="Status"/> is <see cref="DurableJobRunStatus.Running"/>.
    /// </summary>
    [Id(1)]
    public TimeSpan? PollAfterDelay { get; }

    /// <summary>
    /// Gets the exception associated with a failed job execution when <see cref="Status"/> is <see cref="DurableJobRunStatus.Failed"/>.
    /// </summary>
    [Id(2)]
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the time at which the job should be durably rescheduled after the current execution completes successfully.
    /// </summary>
    [Id(3)]
    public DateTimeOffset? RescheduleTime { get; }

    private static readonly DurableJobRunResult CompletedInstance = new(DurableJobRunStatus.Completed, null, null, null);

    /// <summary>
    /// Gets a result indicating the job completed successfully.
    /// </summary>
    public static DurableJobRunResult Completed => CompletedInstance;

    /// <summary>
    /// Creates a result indicating the job is still running and should be polled after the specified delay.
    /// </summary>
    /// <param name="delay">The time to wait before polling the run again.</param>
    /// <returns>A running job result.</returns>
    /// <remarks>
    /// The executor keeps the current run and its concurrency slot active, then polls the receiver again after the delay.
    /// TODO: Add validation for minimum/maximum poll delays to prevent abuse.
    /// TODO: Consider concurrency slot management for long-running polls.
    /// </remarks>
    public static DurableJobRunResult Running(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delay));
        return new(DurableJobRunStatus.Running, delay, null, null);
    }

    /// <summary>
    /// Creates a result indicating that the current execution completed successfully and requested durable rescheduling.
    /// </summary>
    /// <param name="dueTime">The time at which the next execution should become due.</param>
    /// <returns>A durable rescheduling result.</returns>
    /// <remarks>
    /// Rescheduling resets the failure-attempt count, so the next dequeue count is one.
    /// During a mixed-version rolling upgrade, callers emit this result after every durable job executor
    /// understands <see cref="DurableJobRunStatus.RescheduleRequested"/> (serialized value 3).
    /// This rollout order preserves compatibility with executors that understand serialized values 0 through 2.
    /// </remarks>
    public static DurableJobRunResult RescheduleAt(DateTimeOffset dueTime)
    {
        return new(DurableJobRunStatus.RescheduleRequested, null, null, dueTime);
    }

    /// <summary>
    /// Creates a result indicating the job failed.
    /// </summary>
    /// <param name="exception">The exception that caused the failure. This will be passed to the retry policy.</param>
    /// <returns>A failed job result.</returns>
    /// <remarks>
    /// The exception will be passed to the retry callback to determine if the job should be retried.
    /// </remarks>
    public static DurableJobRunResult Failed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(DurableJobRunStatus.Failed, null, exception, null);
    }
}

/// <summary>
/// Represents the status of a durable job execution.
/// </summary>
public enum DurableJobRunStatus
{
    /// <summary>
    /// The job completed successfully and should be removed from the queue.
    /// </summary>
    Completed = 0,

    /// <summary>
    /// The job is still running and should be polled again after the specified delay.
    /// </summary>
    Running = 1,

    /// <summary>
    /// The job failed and should be processed through the retry policy.
    /// </summary>
    Failed = 2,

    /// <summary>
    /// The current execution completed successfully and requested durable rescheduling with its failure-attempt count reset.
    /// </summary>
    /// <remarks>
    /// This value is serialized as 3. During rolling upgrades, emit it after all durable job executors support it.
    /// Newer executors treat unknown disposition values as failures and route them through the configured retry policy.
    /// </remarks>
    RescheduleRequested = 3
}

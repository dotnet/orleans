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
    /// Gets a value indicating whether the job should be polled again after a delay.
    /// </summary>
    [MemberNotNullWhen(true, nameof(PollAfterDelay))]
    public bool IsPending => Status == DurableJobRunStatus.PollAfter;

    /// <summary>
    /// Gets a value indicating whether the job requested durable rescheduling.
    /// </summary>
    [MemberNotNullWhen(true, nameof(RetryAtTime))]
    public bool IsRetryRequested => Status == DurableJobRunStatus.RetryAt;

    private DurableJobRunResult(DurableJobRunStatus status, TimeSpan? pollAfter, Exception? exception, DateTimeOffset? retryAt)
    {
        Status = status;
        PollAfterDelay = pollAfter;
        Exception = exception;
        RetryAtTime = retryAt;
    }

    /// <summary>
    /// Gets the status of the job execution.
    /// </summary>
    [Id(0)]
    public DurableJobRunStatus Status { get; }

    /// <summary>
    /// Gets the delay before the next status check when <see cref="Status"/> is <see cref="DurableJobRunStatus.PollAfter"/>.
    /// </summary>
    [Id(1)]
    public TimeSpan? PollAfterDelay { get; }

    /// <summary>
    /// Gets the exception associated with a failed job execution when <see cref="Status"/> is <see cref="DurableJobRunStatus.Failed"/>.
    /// </summary>
    [Id(2)]
    public Exception? Exception { get; }

    /// <summary>
    /// Gets the time at which a job requesting durable rescheduling should run again.
    /// </summary>
    [Id(3)]
    public DateTimeOffset? RetryAtTime { get; }

    private static readonly DurableJobRunResult CompletedInstance = new(DurableJobRunStatus.Completed, null, null, null);

    /// <summary>
    /// Gets a result indicating the job completed successfully.
    /// </summary>
    public static DurableJobRunResult Completed => CompletedInstance;

    /// <summary>
    /// Creates a result indicating the job should be polled again after the specified delay.
    /// </summary>
    /// <param name="delay">The time to wait before checking the job status again.</param>
    /// <returns>A poll-after job result.</returns>
    /// <remarks>
    /// The job will remain in an inline polling loop without being re-queued.
    /// The polling loop will hold a concurrency slot until the job completes or fails.
    /// TODO: Add validation for minimum/maximum poll delays to prevent abuse.
    /// TODO: Consider concurrency slot management for long-running polls.
    /// </remarks>
    public static DurableJobRunResult PollAfter(TimeSpan delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero, nameof(delay));
        return new(DurableJobRunStatus.PollAfter, delay, null, null);
    }

    /// <summary>
    /// Creates a result indicating that the job should be durably re-queued for the specified time.
    /// </summary>
    public static DurableJobRunResult RetryAt(DateTimeOffset dueTime)
    {
        return new(DurableJobRunStatus.RetryAt, null, null, dueTime);
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
    Completed,

    /// <summary>
    /// The job is still running and should be polled again after the specified delay.
    /// </summary>
    PollAfter,

    /// <summary>
    /// The job failed and should be processed through the retry policy.
    /// </summary>
    Failed,

    /// <summary>
    /// The handler completed its current attempt and requested durable rescheduling.
    /// </summary>
    RetryAt
}

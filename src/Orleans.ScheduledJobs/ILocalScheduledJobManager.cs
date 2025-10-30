using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

/// <summary>
/// Provides functionality for scheduling and managing jobs on the local silo.
/// </summary>
public interface ILocalScheduledJobManager
{
    /// <summary>
    /// Schedules a job to be executed at a specific time on the target grain.
    /// </summary>
    /// <param name="target">The grain identifier of the target grain that will receive the scheduled job.</param>
    /// <param name="jobName">The name of the job for identification purposes.</param>
    /// <param name="dueTime">The date and time when the job should be executed.</param>
    /// <param name="metadata">Optional metadata associated with the job.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation that returns the scheduled job.</returns>
    Task<IScheduledJob> ScheduleJobAsync(GrainId target, string jobName, DateTimeOffset dueTime, IReadOnlyDictionary<string, string>? metadata, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to cancel a previously scheduled job.
    /// </summary>
    /// <param name="job">The scheduled job to cancel.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation that returns <see langword="true"/> if the job was successfully canceled; otherwise, <see langword="false"/>.</returns>
    Task<bool> TryCancelScheduledJobAsync(IScheduledJob job, CancellationToken cancellationToken);
}

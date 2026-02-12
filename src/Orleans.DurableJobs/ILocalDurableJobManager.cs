using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Provides functionality for scheduling and managing jobs on the local silo.
/// </summary>
public interface ILocalDurableJobManager
{
    /// <summary>
    /// Schedules a job to be executed at a specific time on the target grain.
    /// </summary>
    /// <param name="request">The request containing the job scheduling parameters.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation that returns the durable job.</returns>
    Task<DurableJob> ScheduleJobAsync(ScheduleJobRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to cancel a previously scheduled durable job.
    /// </summary>
    /// <param name="job">The durable job to cancel.</param>
    /// <param name="cancellationToken">A cancellation token to cancel the operation.</param>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation that returns <see langword="true"/> if the job was successfully canceled; otherwise, <see langword="false"/>.</returns>
    Task<bool> TryCancelDurableJobAsync(DurableJob job, CancellationToken cancellationToken);
}

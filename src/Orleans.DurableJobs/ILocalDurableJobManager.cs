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
    /// Requests cancellation of a previously scheduled durable job.
    /// </summary>
    /// <param name="job">The durable job for which cancellation is requested.</param>
    /// <param name="requestCancellationToken">
    /// A token which cancels this request operation. It does not cancel a job execution attempt.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> representing the asynchronous operation. The result is <see langword="true"/> when
    /// the cancellation request was durably recorded, preventing future attempts; otherwise, <see langword="false"/>.
    /// An already-running attempt is cooperatively independent of this request and may still complete.
    /// </returns>
    Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken);
}

internal interface ILocalDurableJobManagerSystemTarget : ISystemTarget
{
    Task<bool> CancelAsync(DurableJob job, CancellationToken requestCancellationToken);
}

using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Represents a request to schedule a durable job.
/// </summary>
public readonly struct ScheduleJobRequest
{
    /// <summary>
    /// Gets the grain identifier of the target grain that will receive the durable job.
    /// </summary>
    public required GrainId Target { get; init; }

    /// <summary>
    /// Gets the name of the job for identification purposes.
    /// </summary>
    public required string JobName { get; init; }

    /// <summary>
    /// Gets the date and time when the job should be executed.
    /// </summary>
    public required DateTimeOffset DueTime { get; init; }

    /// <summary>
    /// Gets optional metadata associated with the job.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

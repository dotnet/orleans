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

    /// <summary>
    /// Gets the W3C <c>traceparent</c> value to associate with the scheduled job, used to continue the distributed trace when the job is later executed.
    /// If <see langword="null"/>, the value of <see cref="System.Diagnostics.Activity.Current"/> at the time <see cref="ILocalDurableJobManager.ScheduleJobAsync"/> is invoked will be used.
    /// </summary>
    public string? TraceParent { get; init; }

    /// <summary>
    /// Gets the W3C <c>tracestate</c> value to associate with the scheduled job, used to continue the distributed trace when the job is later executed.
    /// If <see langword="null"/>, the value of <see cref="System.Diagnostics.Activity.Current"/> at the time <see cref="ILocalDurableJobManager.ScheduleJobAsync"/> is invoked will be used.
    /// </summary>
    public string? TraceState { get; init; }
}

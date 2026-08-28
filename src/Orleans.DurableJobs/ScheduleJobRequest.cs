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
    /// Gets an optional stable job identifier.
    /// </summary>
    /// <remarks>
    /// Repeating a request with the same identifier, target, name, and metadata is idempotent within
    /// its due-time shard. The first request determines the due time and trace context.
    /// A conflicting request using the same identifier is rejected.
    /// </remarks>
    internal string? JobId { get; init; }

    /// <summary>
    /// Gets the grain identifier of the target grain that will receive the durable job.
    /// </summary>
    public required GrainId Target { get; init; }

    /// <summary>
    /// Gets the non-empty name of the job for identification and handler routing.
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

    internal void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(JobName, nameof(JobName));
        if (JobId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(JobId, nameof(JobId));
        }
    }

    internal DurableJob CreateJob(string shardId) =>
        new()
        {
            Id = JobId ?? Guid.NewGuid().ToString(),
            TargetGrainId = Target,
            Name = JobName,
            DueTime = DueTime,
            ShardId = shardId,
            Metadata = Metadata,
            TraceParent = TraceParent,
            TraceState = TraceState
        };

    internal bool Matches(DurableJob job) =>
        job.Id == JobId
        && job.TargetGrainId == Target
        && string.Equals(job.Name, JobName, StringComparison.Ordinal)
        && MetadataEquals(job.Metadata, Metadata);

    private static bool MetadataEquals(
        IReadOnlyDictionary<string, string>? left,
        IReadOnlyDictionary<string, string>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var candidate)
                || !string.Equals(value, candidate, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

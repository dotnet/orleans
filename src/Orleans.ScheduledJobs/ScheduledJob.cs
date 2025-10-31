using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

/// <summary>
/// Represents a scheduled job that will be executed at a specific time.
/// </summary>
[GenerateSerializer]
[Alias("Orleans.ScheduledJobs.ScheduledJob")]
public sealed class ScheduledJob
{
    /// <summary>
    /// Gets the unique identifier for this scheduled job.
    /// </summary>
    [Id(0)]
    public required string Id { get; init; }
    
    /// <summary>
    /// Gets the name of the scheduled job.
    /// </summary>
    [Id(1)]
    public required string Name { get; init; }
    
    /// <summary>
    /// Gets the time when this job is due to be executed.
    /// </summary>
    [Id(2)]
    public DateTimeOffset DueTime { get; init; }
    
    /// <summary>
    /// Gets the identifier of the target grain that will handle this job.
    /// </summary>
    [Id(3)]
    public GrainId TargetGrainId { get; init; }
    
    /// <summary>
    /// Gets the identifier of the shard that manages this scheduled job.
    /// </summary>
    [Id(4)]
    public required string ShardId { get; init; }
    
    /// <summary>
    /// Gets optional metadata associated with this scheduled job.
    /// </summary>
    [Id(5)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

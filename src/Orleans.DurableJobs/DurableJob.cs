using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.DurableJobs;

/// <summary>
/// Represents a durable job that will be executed at a specific time.
/// </summary>
[GenerateSerializer]
[Alias("Orleans.DurableJobs.DurableJob")]
public sealed class DurableJob
{
    /// <summary>
    /// Gets the unique identifier for this durable job.
    /// </summary>
    [Id(0)]
    public required string Id { get; init; }
    
    /// <summary>
    /// Gets the name of the durable job.
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
    /// Gets the identifier of the shard that manages this durable job.
    /// </summary>
    [Id(4)]
    public required string ShardId { get; init; }
    
    /// <summary>
    /// Gets optional metadata associated with this durable job.
    /// </summary>
    [Id(5)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

using System;
using System.Collections.Generic;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

public interface IScheduledJob
{
    string Id { get; }
    string Name { get; }
    DateTimeOffset DueTime { get; }
    GrainId TargetGrainId { get; }
    IReadOnlyDictionary<string, string>? Metadata { get; }
}

[GenerateSerializer]
[Alias("Orleans.ScheduledJobs.ScheduledJob")]
public sealed class ScheduledJob : IScheduledJob
{
    [Id(0)]
    public required string Id { get; init; }
    [Id(1)]
    public required string Name { get; init; }
    [Id(2)]
    public DateTimeOffset DueTime { get; init; }
    [Id(3)]
    public GrainId TargetGrainId { get; init; }
    [Id(4)]
    public required string ShardId { get; init; }
    [Id(5)]
    public IReadOnlyDictionary<string, string>? Metadata { get; init; }
}

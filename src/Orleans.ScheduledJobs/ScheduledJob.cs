using System;
using Orleans.Runtime;

namespace Orleans.ScheduledJobs;

public interface IScheduledJob
{
    string Id { get; init; }
    string Name { get; init; }
    DateTimeOffset DueTime { get; init; }
    GrainId TargetGrainId { get; init; }
}

[GenerateSerializer]
[Alias("Orleans.ScheduledJobs.ScheduledJob")]
public class ScheduledJob : IScheduledJob
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
}

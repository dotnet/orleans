using System;
using Orleans.Runtime;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Rebalancing statistics for the given <see cref="SiloAddress"/>.
/// </summary>
/// <remarks>
/// Used for diagnostics / metrics purposes. Note that statistics are an approximation.</remarks>
[GenerateSerializer, Immutable, Alias("RebalancingStatistics")]
public readonly struct RebalancingStatistics
{
    /// <summary>
    /// The time these statistics were assembled.
    /// </summary>
    [Id(0)] public required DateTime TimeStamp { get; init; }

    /// <summary>
    /// The silo address.
    /// </summary>
    [Id(1)] public required SiloAddress SiloAddress { get; init; }

    /// <summary>
    /// The number of activations that have been dispersed from this silo thus far.
    /// </summary>
    [Id(2)] public required ulong DispersedActivations { get; init; }

    /// <summary>
    /// The number of activations that have been acquired by this silo thus far.
    /// </summary>
    [Id(3)] public required ulong AcquiredActivations { get; init; }
}

using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Current state of the <see cref="IActivationRebalancerWorker"/>.
/// </summary>
/// <param name="Address">The silo where the rebalancer is currently located.</param>
/// <param name="Status">The current status of the rebalancer.</param>
/// <param name="Duration">If suspended, shows for how long (<see langword="null"/> means indefinitely).</param>
/// <param name="Statistics">Latest rebalancing statistics.</param>
[GenerateSerializer, Immutable, Alias("RebalancerReport")]
internal readonly record struct RebalancerReport(
    SiloAddress Address, RebalancerStatus Status, TimeSpan? Duration,
    ImmutableArray<RebalancingStatistics> Statistics);

/// <summary>
/// The status of the <see cref="IActivationRebalancerWorker"/>.
/// </summary>
[GenerateSerializer]
internal enum RebalancerStatus
{
    Executing,
    Suspended
}

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
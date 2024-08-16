using System;
using System.Collections.Immutable;
using Orleans.Runtime;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// The status of the <see cref="IActivationRebalancerWorker"/>.
/// </summary>
[GenerateSerializer]
public enum RebalancerStatus : byte
{
    /// <summary>
    /// It is executing.
    /// </summary>
    Executing = 0,
    /// <summary>
    /// It is suspended.
    /// </summary>
    Suspended = 1
}

/// <summary>
/// A report of the current state of the activation rebalancer.
/// </summary>
[GenerateSerializer, Immutable, Alias("RebalancingReport")]
public readonly struct RebalancingReport
{
    /// <summary>
    /// The silo where the rebalancer is currently located.
    /// </summary>
    [Id(0)] public required SiloAddress Host { get; init; }

    /// <summary>
    /// The current status of the rebalancer.
    /// </summary>
    [Id(1)] public required RebalancerStatus Status { get; init; }

    /// <summary>
    /// The amount of time the rebalancer is suspended (if at all).
    /// </summary>
    /// <remarks>This will always be <see langword="null"/> if <see cref="Status"/> is <see cref="RebalancerStatus.Executing"/>.</remarks>
    [Id(2)] public TimeSpan? SuspensionDuration { get; init; }

    /// <summary>
    /// The current view of the cluster's imbalance.
    /// </summary>
    /// <remarks>Range: [0-1]</remarks>
    [Id(3)] public required double ClusterImbalance { get; init; }

    /// <summary>
    /// Latest rebalancing statistics.
    /// </summary>
    [Id(4)] public required ImmutableArray<RebalancingStatistics> Statistics { get; init; }
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
    /// The silo to which these statistics belong to.
    /// </summary>
    [Id(1)] public required SiloAddress SiloAddress { get; init; }

    /// <summary>
    /// The approximate number of activations that have been dispersed from this silo thus far.
    /// </summary>
    [Id(2)] public required ulong DispersedActivations { get; init; }

    /// <summary>
    /// The approximate number of activations that have been acquired by this silo thus far.
    /// </summary>
    [Id(3)] public required ulong AcquiredActivations { get; init; }
}
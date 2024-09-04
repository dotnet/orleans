using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Internal;
using Orleans.Runtime;

namespace Orleans.Placement;

/// <summary>
/// Interface for management of activation rebalancing.
/// </summary>
[Alias("IActivationRebalancerGrain")]
public interface IActivationRebalancerGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// The key to the only activation of this grain type.
    /// </summary>
    /// <remarks><strong>Attempts to create another activation of this
    /// type will result in an <see cref="InvalidOperationException"/>.</strong></remarks>
    public const int Key = 0;

    [AlwaysInterleave, Alias("GetStatistics")]
    ValueTask<ImmutableArray<RebalancingStatistics>> GetStatistics();

    /// <summary>
    /// Resumes the rebalancer if its suspended, otherwise its a no-op.
    /// </summary>
    [Alias("ResumeRebalancing")] Task ResumeRebalancing();
    /// <summary>
    /// Suspends the rebalancer if its operating, otherwise its a no-op.
    /// </summary>
    /// <param name="duration">
    /// The amount of time to suspend the rebalancer.
    /// <para><see langword="null"/> means suspend indefinitely.</para>
    /// </param>
    [Alias("SuspendRebalancing")] Task SuspendRebalancing(TimeSpan? duration);
}

[Alias("IInternalActivationRebalancerGrain")]
internal interface IInternalActivationRebalancerGrain : IActivationRebalancerGrain
{
    /// <summary>
    /// Starts the rebalancer if its not started yet, otherwise its a no-op.
    /// </summary>
    [Alias("StartRebalancer")] Task StartRebalancer();
}

/// <summary>
/// Rebalancing statistics for the given <see cref="SiloAddress"/>.
/// </summary>
/// <remarks>
/// Used for diagnostics / metrics purposes. Note that statistics are an approximation.</remarks>
[GenerateSerializer]
[Alias("RebalancingStatistics")]
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

/// <summary>
/// Determines how long to wait between successive rebalancing sessions, if an aprior session has failed.
/// </summary>
/// <remarks>
/// A session is considered "failed" if n-consecutive number of cycles yielded no significant improvement
/// to the cluster's entropy.
/// </remarks>
public interface IFailedRebalancingSessionBackoffProvider : IBackoffProvider { }
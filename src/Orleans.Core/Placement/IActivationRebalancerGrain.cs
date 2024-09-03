using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Tasks;
using Orleans.Concurrency;
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
    ValueTask<ImmutableArray<SiloRebalancingStatistics>> GetStatistics();

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
/// Rebalancing statistics for the given <paramref name="SiloAddress"/>.
/// </summary>
/// <param name="TimeStamp">The time these statistics were assembled.</param>
/// <param name="SiloAddress">The silo address.</param>
/// <param name="DispersedActivations">The number of activations that have been dispersed from this silo thus far.</param>
/// <param name="AcquiredActivations">The number of activations that have been acquired by this silo thus far.</param>
/// <remarks>Used for diagnostics / metrics purposes.</remarks>
[GenerateSerializer]
[Alias("SiloRebalancingStatistics")]
public readonly record struct SiloRebalancingStatistics(
    DateTime TimeStamp,
    SiloAddress SiloAddress,
    ulong DispersedActivations,
    ulong AcquiredActivations);

/// <summary>
/// Determines how long to wait between successive rebalancing sessions, if an aprior session has failed.
/// </summary>
/// <remarks>A session is considered "failed" if it did not yield any significant improvement to the cluster's entropy.</remarks>
public interface IFailedRebalancingSessionBackoffProvider
{
    /// <summary>
    /// The minimum amount of time to wait before attempting a subsequent rebalancing sessions.
    /// </summary>
    /// <param name="attempt">The number of consecutive failed sessions which have been made.</param>
    TimeSpan Next(uint attempt);
}
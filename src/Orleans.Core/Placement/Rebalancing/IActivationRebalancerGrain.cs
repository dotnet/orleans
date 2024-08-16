using System;
using System.Threading.Tasks;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Interface for management of activation rebalancing.
/// </summary>
[Alias("IActivationRebalancerGrain")]
public interface IActivationRebalancerGrain : IGrainWithIntegerKey
{
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
    /// <summary>
    /// Trigger a new rebalancing session given <paramref name="parameters"/>.
    /// </summary>
    /// <param name="parameters">The rebalancing parameters to adhere to.</param>
    /// <remarks>If a session exists, it will be dropped and a new one will start.</remarks>
    [Alias("TriggerRebalancing")] Task TriggerRebalancing(RebalancingParameters parameters);
}

/// <summary>
/// Parameters to control the <see cref="IActivationRebalancerGrain"/> execution.
/// </summary>
[Immutable, GenerateSerializer, Alias("Orleans.Placement.Rebalancing.RebalancingParameters")]
public readonly record struct RebalancingParameters(
    TimeSpan SessionCyclePeriod,
    int MaxStaleCycles,
    float EntropyQuantum,
    float MaxEntropyDeviation,
    float CycleNumberWeight,
    float SiloNumberWeight);
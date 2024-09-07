using System;
using System.Threading.Tasks;
using Orleans.Concurrency;
using Orleans.Runtime;

namespace Orleans.Placement.Rebalancing;

[Alias("IActivationRebalancerWorker")]
internal interface IActivationRebalancerWorker : IGrainWithIntegerKey
{
    /// <summary>
    /// Returns latest report.
    /// </summary>
    [AlwaysInterleave, Alias("GetReport")]
    ValueTask<RebalancerReport> GetReport();

    /// <summary>
    /// Starts the rebalancer if its not started yet, otherwise its a no-op.
    /// </summary>
    /// <returns>The host address where the rebalancer is activated.</returns>
    [Alias("StartRebalancer")] ValueTask<RebalancerReport> StartRebalancer();

    /// <summary>
    /// Resumes rebalancing if its suspended, otherwise its a no-op.
    /// </summary>
    [Alias("ResumeRebalancing")] Task ResumeRebalancing();
    /// <summary>
    /// Suspends rebalancing if its running, otherwise its a no-op.
    /// </summary>
    /// <param name="duration">
    /// The amount of time to suspend the rebalancer.
    /// <para><see langword="null"/> means suspend indefinitely.</para>
    /// </param>
    [Alias("SuspendRebalancing")] Task SuspendRebalancing(TimeSpan? duration);
}
using System;
using System.Threading.Tasks;
using Orleans.Concurrency;

namespace Orleans.Placement.Rebalancing;

[Alias("IActivationRebalancerWorker")]
internal interface IActivationRebalancerWorker : IGrainWithIntegerKey
{
    /// <summary>
    /// Returns latest report.
    /// </summary>
    [AlwaysInterleave, Alias("GetReport")] ValueTask<RebalancingReport> GetReport();

    /// <summary>
    /// Wakes up the rebalancer and starts it if its not started yet.
    /// </summary>
    /// <returns>The most recent rebalancing report.</returns>
    [Alias("StartRebalancer")] ValueTask<RebalancingReport> Ping();

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
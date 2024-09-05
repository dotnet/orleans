using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Orleans.Runtime;

#nullable enable

namespace Orleans.Placement.Rebalancing;

[Alias("IActivationRebalancerMonitor")]
internal interface IActivationRebalancerMonitor : ISystemTarget, IActivationRebalancer
{
    /// <summary>
    /// The period on which the <see cref="IActivationRebalancerWorker"/> must report back to the monitor.
    /// </summary>
    public static readonly TimeSpan WorkerReportPeriod = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Returns the current host of the <see cref="IActivationRebalancerWorker"/>.
    /// </summary>
    /// <remarks>Currently only used for testing</remarks>
    [Alias("GetRebalancerHost")] ValueTask<SiloAddress> GetRebalancerHost();

    /// <summary>
    /// Invoked periodically by the <see cref="IActivationRebalancerWorker"/>.
    /// </summary>
    /// <param name="address">The silo where the rebalancer is currently located.</param>
    /// <param name="statistics">Latest rebalancing statistics.</param>
    [Alias("Report")] Task Report(SiloAddress address, ImmutableArray<RebalancingStatistics> statistics);
}

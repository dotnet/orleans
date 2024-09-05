using System;
using System.Collections.Immutable;
using System.Threading.Tasks;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Interface for management of activation rebalancing.
/// </summary>
public interface IActivationRebalancer
{
    /// <inheritdoc cref="IActivationRebalancerWorker.ResumeRebalancing"/>
    Task ResumeRebalancing();

    /// <inheritdoc cref="IActivationRebalancerWorker.SuspendRebalancing(TimeSpan?)"/>
    Task SuspendRebalancing(TimeSpan? duration);

    /// <summary>
    /// Returns rebalancing statistics.
    /// <para>Statistics can lag behind if you choose a session cycle period less than <see cref="IActivationRebalancerMonitor.WorkerReportPeriod"/>.</para>
    /// </summary>
    /// <param name="force">If set to <see langword="true"/> returns the most current statistics.</param>
    /// <remarks>Using <paramref name="force"/> incurs an asynchronous operation.</remarks>
    ValueTask<ImmutableArray<RebalancingStatistics>> GetStatistics(bool force = false);
}

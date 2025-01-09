using System;
using System.Threading.Tasks;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// A gateway to interface with the activation rebalancer.
/// </summary>
/// <remarks>This is available only on the silo.</remarks>
public interface IActivationRebalancer
{
    /// <summary>
    /// Returns the rebalancing report.
    /// <para>The report can lag behind if you choose a session cycle period less than <see cref="IActivationRebalancerMonitor.WorkerReportPeriod"/>.</para>
    /// </summary>
    /// <param name="force">If set to <see langword="true"/> returns the most current report.</param>
    /// <remarks>Using <paramref name="force"/> incurs an asynchronous operation.</remarks>
    ValueTask<RebalancingReport> GetRebalancingReport(bool force = false);

    /// <inheritdoc cref="IActivationRebalancerWorker.ResumeRebalancing"/>
    Task ResumeRebalancing();

    /// <inheritdoc cref="IActivationRebalancerWorker.SuspendRebalancing(TimeSpan?)"/>
    Task SuspendRebalancing(TimeSpan? duration = null);

    /// <summary>
    /// Subscribe to activation rebalancer reports.
    /// </summary>
    /// <param name="listener">The component that will be notified.</param>
    void SubscribeToReports(IActivationRebalancerReportListener listener);

    /// <summary>
    /// Unsubscribe from activation rebalancer reports.
    /// </summary>
    /// <param name="listener">The already subscribed component.</param>
    void UnsubscribeFromReports(IActivationRebalancerReportListener listener);
}

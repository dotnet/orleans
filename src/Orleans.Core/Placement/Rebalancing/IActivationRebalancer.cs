using System;
using System.Threading.Tasks;

namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Provides functionalities for interfacing with the activation rebalancer.
/// </summary>
public interface IActivationRebalancer
{
    /// <summary>
    /// Returns the rebalancer report.
    /// <para>Reports can lag behind if you choose a session cycle period less than <see cref="IActivationRebalancerMonitor.WorkerReportPeriod"/>.</para>
    /// </summary>
    /// <param name="force">If set to <see langword="true"/> returns the most current report.</param>
    /// <remarks>Using <paramref name="force"/> incurs an asynchronous operation.</remarks>
    ValueTask<RebalancerReport> GetRebalancerReport(bool force = false);

    /// <inheritdoc cref="IActivationRebalancerWorker.ResumeRebalancing"/>
    Task ResumeRebalancing();

    /// <inheritdoc cref="IActivationRebalancerWorker.SuspendRebalancing(TimeSpan?)"/>
    Task SuspendRebalancing(TimeSpan? duration);

    /// <summary>
    /// Subscribe to activation rebalancer status changes.
    /// </summary>
    /// <param name="listener">The component that will be notified.</param>
    void SubscribeToStatusChanges(IActivationRebalancerReportListener listener);

    /// <summary>
    /// Unsubscribe from activation rebalancer status changes.
    /// </summary>
    /// <param name="listener">The already subscribed component.</param>
    void UnsubscribeFromStatusChanges(IActivationRebalancerReportListener listener);
}

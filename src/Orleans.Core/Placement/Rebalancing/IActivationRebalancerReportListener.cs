namespace Orleans.Placement.Rebalancing;

/// <summary>
/// Interface for types which listen to rebalancer status changes.
/// </summary>
public interface IActivationRebalancerReportListener
{
    /// <summary>
    /// Triggered when rebalancer has provided a new <see cref="RebalancerReport"/>.
    /// </summary>
    /// <param name="report">Latest report from the rebalancer.</param>
    void OnReport(RebalancerReport report);
}
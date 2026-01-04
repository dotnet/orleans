#nullable enable

using System;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Provides health information based on probe/ping activity for local silo health monitoring.
/// </summary>
/// <remarks>
/// <para>
/// This interface abstracts the probe-related health checks used by <see cref="LocalSiloHealthMonitor"/>
/// to determine if the local silo is healthy based on probe activity.
/// </para>
/// <para>
/// Orleans supports two implementations:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///       <b>Default probe monitoring</b>: Uses Orleans' built-in ping protocol to track
///       received probe requests and responses. This is used with the standard
///       <see cref="ClusterHealthMonitor"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>External failure detection</b>: When using an external membership system
///       (such as RapidCluster) that provides its own failure detection protocol,
///       a no-op or custom implementation should be registered. The external system's
///       probe/heartbeat activity can be surfaced through this interface.
///     </description>
///   </item>
/// </list>
/// </remarks>
internal interface IProbeHealthMonitor
{
    /// <summary>
    /// Gets the health degradation score based on received probe requests.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="activeNodeCount">The number of active nodes in the cluster.</param>
    /// <param name="complaint">If returning a non-zero score, a description of the issue.</param>
    /// <returns>
    /// A score of 0 if healthy, or a positive integer indicating degradation level.
    /// Higher values indicate more severe degradation.
    /// </returns>
    int CheckReceivedProbeRequests(DateTime now, int activeNodeCount, out string? complaint);

    /// <summary>
    /// Gets the health degradation score based on received probe responses.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="monitoredNodeCount">The number of nodes being monitored by this silo.</param>
    /// <param name="complaint">If returning a non-zero score, a description of the issue.</param>
    /// <returns>
    /// A score of 0 if healthy, or a positive integer indicating degradation level.
    /// Higher values indicate more severe degradation.
    /// </returns>
    int CheckReceivedProbeResponses(DateTime now, int monitoredNodeCount, out string? complaint);
}

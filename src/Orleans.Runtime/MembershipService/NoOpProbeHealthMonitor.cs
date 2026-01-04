#nullable enable

using System;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// A no-op implementation of <see cref="IProbeHealthMonitor"/> for use with external
/// failure detection systems.
/// </summary>
/// <remarks>
/// <para>
/// When using an external membership system that provides its own failure detection
/// (such as RapidCluster's consensus-based detection), register this implementation
/// to disable Orleans' probe-based health checks in <see cref="LocalSiloHealthMonitor"/>.
/// </para>
/// <para>
/// This implementation always reports healthy (score of 0) since the external system
/// handles probe/heartbeat monitoring through its own protocol.
/// </para>
/// <para>
/// For more sophisticated integration, external systems can provide their own
/// <see cref="IProbeHealthMonitor"/> implementation that surfaces health information
/// from their failure detector.
/// </para>
/// </remarks>
internal sealed class NoOpProbeHealthMonitor : IProbeHealthMonitor
{
    /// <inheritdoc/>
    public int CheckReceivedProbeRequests(DateTime now, int activeNodeCount, out string? complaint)
    {
        complaint = null;
        return 0;
    }

    /// <inheritdoc/>
    public int CheckReceivedProbeResponses(DateTime now, int monitoredNodeCount, out string? complaint)
    {
        complaint = null;
        return 0;
    }
}

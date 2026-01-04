#nullable enable

using System;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Default implementation of <see cref="IProbeHealthMonitor"/> that uses Orleans' built-in
/// ping protocol to monitor probe health.
/// </summary>
/// <remarks>
/// This implementation tracks:
/// <list type="bullet">
///   <item><description>Received probe requests via <see cref="ProbeRequestMonitor"/></description></item>
///   <item><description>Received probe responses via <see cref="IClusterHealthMonitor.SiloMonitors"/></description></item>
/// </list>
/// </remarks>
internal sealed partial class DefaultProbeHealthMonitor : IProbeHealthMonitor
{
    private readonly ProbeRequestMonitor _probeRequestMonitor;
    private readonly IClusterHealthMonitor _clusterHealthMonitor;
    private readonly ClusterMembershipOptions _clusterMembershipOptions;
    private readonly ILogger<DefaultProbeHealthMonitor> _logger;

    public DefaultProbeHealthMonitor(
        ProbeRequestMonitor probeRequestMonitor,
        IClusterHealthMonitor clusterHealthMonitor,
        IOptions<ClusterMembershipOptions> clusterMembershipOptions,
        ILogger<DefaultProbeHealthMonitor> logger)
    {
        _probeRequestMonitor = probeRequestMonitor;
        _clusterHealthMonitor = clusterHealthMonitor;
        _clusterMembershipOptions = clusterMembershipOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public int CheckReceivedProbeRequests(DateTime now, int activeNodeCount, out string? complaint)
    {
        complaint = null;

        // Only consider recency of the last received probe request if there is more than one other node.
        // Otherwise, it may fail to vote another node dead in a one or two node cluster.
        if (activeNodeCount <= 2)
        {
            return 0;
        }

        var sinceLastProbeRequest = _probeRequestMonitor.ElapsedSinceLastProbeRequest;
        var recencyWindow = _clusterMembershipOptions.ProbeTimeout.Multiply(_clusterMembershipOptions.NumMissedProbesLimit);

        if (!sinceLastProbeRequest.HasValue)
        {
            LogNoProbeRequests();
            complaint = "This silo has not received any probe requests";
            return 1;
        }

        if (sinceLastProbeRequest.Value > recencyWindow)
        {
            var lastRequestTime = now - sinceLastProbeRequest.Value;
            LogNoRecentProbeRequest(lastRequestTime);
            complaint = $"This silo has not received a probe request since {lastRequestTime}";
            return 1;
        }

        return 0;
    }

    /// <inheritdoc/>
    public int CheckReceivedProbeResponses(DateTime now, int monitoredNodeCount, out string? complaint)
    {
        complaint = null;

        // Determine how recently the latest successful ping response was received.
        var siloMonitors = _clusterHealthMonitor.SiloMonitors;
        var elapsedSinceLastResponse = default(TimeSpan?);

        foreach (var monitor in siloMonitors.Values)
        {
            var current = monitor.ElapsedSinceLastResponse;
            if (current.HasValue && (!elapsedSinceLastResponse.HasValue || current.Value < elapsedSinceLastResponse.Value))
            {
                elapsedSinceLastResponse = current.Value;
            }
        }

        // Only consider recency of the last successful ping if this node is monitoring more than one other node.
        // Otherwise, it may fail to vote another node dead in a one or two node cluster.
        if (siloMonitors.Count <= 1)
        {
            return 0;
        }

        var recencyWindow = _clusterMembershipOptions.ProbeTimeout.Multiply(_clusterMembershipOptions.NumMissedProbesLimit);

        if (!elapsedSinceLastResponse.HasValue)
        {
            LogNoProbeResponses();
            complaint = "This silo has not received any successful probe responses";
            return 1;
        }

        if (elapsedSinceLastResponse.Value > recencyWindow)
        {
            LogNoRecentProbeResponse(elapsedSinceLastResponse.Value);
            complaint = $"This silo has not received a successful probe response since {elapsedSinceLastResponse.Value}";
            return 1;
        }

        return 0;
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "This silo has not received any probe requests"
    )]
    private partial void LogNoProbeRequests();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "This silo has not received a probe request since {LastProbeRequest}"
    )]
    private partial void LogNoRecentProbeRequest(DateTime lastProbeRequest);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "This silo has not received any successful probe responses"
    )]
    private partial void LogNoProbeResponses();

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "This silo has not received a successful probe response since {LastSuccessfulResponse}"
    )]
    private partial void LogNoRecentProbeResponse(TimeSpan lastSuccessfulResponse);
}

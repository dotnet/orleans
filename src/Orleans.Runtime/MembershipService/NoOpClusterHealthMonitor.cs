#nullable enable

using System;
using System.Collections.Immutable;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// A no-op implementation of <see cref="IClusterHealthMonitor"/> for use with external
/// failure detection systems like RapidCluster.
/// </summary>
/// <remarks>
/// <para>
/// When using an external membership system that provides its own failure detection
/// (such as RapidCluster's consensus-based detection), register this implementation
/// to disable Orleans' built-in probe-based monitoring.
/// </para>
/// <para>
/// This is the correct approach for external failure detection - the external system
/// handles detecting failures and reports membership changes through <see cref="IMembershipManager"/>.
/// Orleans' probe machinery is not needed and would be wasteful.
/// </para>
/// </remarks>
internal sealed partial class NoOpClusterHealthMonitor : IClusterHealthMonitor
{
    private readonly ILogger<NoOpClusterHealthMonitor> _logger;

    public NoOpClusterHealthMonitor(ILogger<NoOpClusterHealthMonitor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public ImmutableDictionary<SiloAddress, SiloHealthMonitor> SiloMonitors
        => ImmutableDictionary<SiloAddress, SiloHealthMonitor>.Empty;

    /// <inheritdoc/>
    void ILifecycleParticipant<ISiloLifecycle>.Participate(ISiloLifecycle lifecycle)
    {
        lifecycle.Subscribe(
            nameof(NoOpClusterHealthMonitor),
            ServiceLifecycleStage.Active,
            _ =>
            {
                LogExternalFailureDetectionActive(_logger);
                return System.Threading.Tasks.Task.CompletedTask;
            },
            _ => System.Threading.Tasks.Task.CompletedTask);
    }

    /// <inheritdoc/>
    bool IHealthCheckable.CheckHealth(DateTime lastCheckTime, out string? reason)
    {
        // External failure detection is always considered healthy from Orleans' perspective.
        // The external system (e.g., RapidCluster) is responsible for health monitoring.
        reason = null;
        return true;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "External failure detection is active. Orleans probe-based health monitoring is disabled."
    )]
    private static partial void LogExternalFailureDetectionActive(ILogger logger);
}

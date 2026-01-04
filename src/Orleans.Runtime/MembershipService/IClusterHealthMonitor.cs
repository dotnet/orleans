#nullable enable

using System.Collections.Immutable;
using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Responsible for monitoring the health of silos in the cluster and detecting failures.
/// </summary>
/// <remarks>
/// <para>
/// Orleans supports two failure detection models:
/// </para>
/// <list type="number">
///   <item>
///     <description>
///       <b>Built-in probe-based detection</b> (default): Orleans silos actively probe each other
///       using an expander graph topology. When a silo misses too many probes, it is suspected
///       and eventually declared dead. This is implemented by <see cref="ClusterHealthMonitor"/>.
///     </description>
///   </item>
///   <item>
///     <description>
///       <b>External failure detection</b>: An external membership system (such as RapidCluster)
///       handles failure detection through its own protocol. In this case, register a no-op
///       implementation to disable Orleans' built-in probing while allowing the external system
///       to manage membership changes through <see cref="IMembershipManager"/>.
///     </description>
///   </item>
/// </list>
/// <para>
/// When using external failure detection, liveness IS still enabled - it's just handled by
/// the external system rather than Orleans' probe-based mechanism. This is different from
/// <c>ClusterMembershipOptions.LivenessEnabled = false</c>, which was a workaround that
/// disabled the DeclareDead step without properly disabling the probing infrastructure.
/// </para>
/// </remarks>
internal interface IClusterHealthMonitor : ILifecycleParticipant<ISiloLifecycle>, IHealthCheckParticipant
{
    /// <summary>
    /// Gets the collection of silos currently being monitored by this silo.
    /// </summary>
    /// <remarks>
    /// For external failure detection systems, this returns an empty dictionary since
    /// monitoring is handled externally.
    /// </remarks>
    ImmutableDictionary<SiloAddress, SiloHealthMonitor> SiloMonitors { get; }
}

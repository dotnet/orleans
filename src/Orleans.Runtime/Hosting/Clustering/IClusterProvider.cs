using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.Hosting.Clustering;

/// <summary>
/// Provides cluster membership information from an external hosting environment.
/// </summary>
public interface IClusterProvider
{
    /// <summary>
    /// Lists the members which currently belong to the external cluster.
    /// </summary>
    /// <param name="cancellation">The cancellation token.</param>
    /// <returns>The current external cluster members.</returns>
    /// <remarks>
    /// The result includes the local silo and marks it using <see cref="ExternalClusterMember.IsCurrentSilo"/>.
    /// </remarks>
    Task<IEnumerable<ExternalClusterMember>> ListMembersAsync(CancellationToken cancellation);

    /// <summary>
    /// Monitors changes to external cluster membership until cancellation is requested.
    /// </summary>
    /// <param name="cancellation">The cancellation token which ends the active monitoring tenure.</param>
    /// <returns>A stream of external cluster membership events.</returns>
    IAsyncEnumerable<ClusterEvent> MonitorChangesAsync(CancellationToken cancellation);

    /// <summary>
    /// Returns a provider-specific description of the named external member.
    /// </summary>
    /// <param name="name">The external member name.</param>
    /// <returns>A description suitable for diagnostics.</returns>
    string Describe(string name);

    /// <summary>
    /// Deletes the named external member.
    /// </summary>
    /// <param name="name">The external member name.</param>
    /// <param name="cancellation">The cancellation token.</param>
    /// <returns>A task which represents the operation.</returns>
    /// <remarks>
    /// Implementations treat an already absent member as a successful deletion.
    /// </remarks>
    Task DeleteAsync(string name, CancellationToken cancellation);
}

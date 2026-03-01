#nullable enable
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime.MembershipService;

/// <summary>
/// Manages cluster membership state and lifecycle for the local silo.
/// This is the primary abstraction for membership management, supporting
/// both passive (table-based) and active (consensus-based) implementations.
/// </summary>
internal interface IMembershipManager
{
    /// <summary>
    /// Gets the current membership snapshot.
    /// </summary>
    MembershipTableSnapshot CurrentSnapshot { get; }

    /// <summary>
    /// Gets a stream of membership updates.
    /// Updates are published whenever the membership view changes.
    /// Consumers should process updates in order.
    /// </summary>
    IAsyncEnumerable<MembershipTableSnapshot> MembershipUpdates { get; }

    /// <summary>
    /// Gets the local silo's current status.
    /// </summary>
    SiloStatus LocalSiloStatus { get; }

    /// <summary>
    /// Updates the local silo's status in the cluster.
    /// </summary>
    /// <param name="status">The new status.</param>
    /// <returns>A task that completes when the status is updated.</returns>
    /// <exception cref="OrleansException">If the update fails.</exception>
    Task UpdateLocalStatus(SiloStatus status);

    /// <summary>
    /// Requests that a silo be declared dead.
    /// For passive systems, this triggers the voting/kill process.
    /// For active systems, this may trigger a view change proposal.
    /// </summary>
    /// <param name="silo">The silo to kill.</param>
    /// <returns>True if the silo was killed, false otherwise.</returns>
    Task<bool> TryKillSilo(SiloAddress silo);

    /// <summary>
    /// Requests that a silo be suspected (may lead to killing).
    /// For passive systems, this adds a vote.
    /// For active systems, this may be a no-op (FD handles this).
    /// </summary>
    /// <param name="silo">The silo to suspect.</param>
    /// <param name="indirectProbingSilo">Optional: silo that also detected the failure.</param>
    /// <returns>True if action was taken.</returns>
    Task<bool> TrySuspectSilo(SiloAddress silo, SiloAddress indirectProbingSilo = null!);

    /// <summary>
    /// Refreshes the membership view from the source of truth.
    /// For passive systems, this reads from the membership table.
    /// For active systems, this may be a no-op or request latest view.
    /// </summary>
    /// <param name="targetVersion">
    /// Optional target version to wait for. If specified, the method will
    /// continue refreshing until the membership reaches at least this version.
    /// </param>
    Task Refresh(MembershipVersion? targetVersion = null);

    /// <summary>
    /// Processes a membership snapshot received via gossip.
    /// </summary>
    /// <param name="snapshot">The gossiped snapshot.</param>
    Task ProcessGossipSnapshot(MembershipTableSnapshot snapshot);

    /// <summary>
    /// Updates the "I Am Alive" timestamp for the local silo.
    /// For passive systems, this writes to the membership table.
    /// For active systems, this may be a no-op (heartbeats handled differently).
    /// </summary>
    Task UpdateIAmAlive();
}

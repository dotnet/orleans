using System.Collections.Generic;
using System.Collections.Immutable;
using Orleans.Runtime.Messaging;

namespace Orleans.Runtime.GrainDirectory
{
    /// <summary>
    /// An immutable snapshot of the local client routing table.
    /// </summary>
    internal sealed class ClientRoutingTableSnapshot
    {
        public ClientRoutingTableSnapshot(
            MembershipVersion membershipVersion,
            ImmutableDictionary<GrainId, List<ActivationAddress>> routes,
            IRemoteClientDirectory[] remoteSilos,
            long connectedClientsVersion)
        {
            RemoteDirectories = remoteSilos;
            MembershipVersion = membershipVersion;
            Routes = routes;
            ConnectedClientsVersion = connectedClientsVersion;
        }

        /// <summary>
        /// The routes to each client, in a form which facilitates efficient per-client lookups.
        /// </summary>
        public ImmutableDictionary<GrainId, List<ActivationAddress>> Routes { get; }

        /// <summary>
        /// The membership version at the time the snapshot was captured, used for tracking changes.
        /// </summary>
        public MembershipVersion MembershipVersion { get; }

        /// <summary>
        /// The <see cref="IConnectedClientCollection.Version" /> value reflected by this snapshot, used for tracking changes.
        /// </summary>
        public long ConnectedClientsVersion { get; }

        /// <summary>
        /// The available remote directories, used for performing remote lookups and disseminating updates.
        /// </summary>
        public IRemoteClientDirectory[] RemoteDirectories { get; }
    }
}

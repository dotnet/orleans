using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface IRemoteClientDirectory : ISystemTarget
    {
        [Alias("972F9953")]
        Task OnUpdateClientRoutes(
            ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update,
            CancellationToken cancellationToken = default);

        [Alias("A6E49CD1")]
        Task<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>> GetClientRoutes(
            ImmutableDictionary<SiloAddress, long> knownRoutes,
            CancellationToken cancellationToken = default);
    }
}

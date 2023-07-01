using System.Collections.Immutable;

namespace Orleans.Runtime.GrainDirectory
{
    internal interface IRemoteClientDirectory : ISystemTarget
    {
        Task OnUpdateClientRoutes(ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)> update);
        Task<ImmutableDictionary<SiloAddress, (ImmutableHashSet<GrainId> ConnectedClients, long Version)>> GetClientRoutes(ImmutableDictionary<SiloAddress, long> knownRoutes);
    }
}

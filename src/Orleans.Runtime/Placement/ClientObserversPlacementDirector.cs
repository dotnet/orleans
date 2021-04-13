using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// ClientObserversPlacementDirector is used to prevent placement of client observer activations.
    /// </summary>
    internal class ClientObserversPlacementDirector : IPlacementDirector
    {
        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context) => throw new ClientNotAvailableException(target.GrainIdentity);
    }
}

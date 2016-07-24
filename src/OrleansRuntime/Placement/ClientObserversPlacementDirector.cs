using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// ClientObserversPlacementDirector is a director for routing requests to client observers.
    /// It uses RandomPlacementDirector.OnSelectActivation for looking up the activation in the directory 
    /// (looking up the gateway address that can forward that request to the client).
    /// It does not allow placing client observer activations.
    /// </summary>
    internal class ClientObserversPlacementDirector : RandomPlacementDirector
    {
        internal override Task<PlacementResult> 
            OnAddActivation(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            throw new InvalidOperationException("Client Observers are not activated using the placement subsystem. Grain " + grain);
        }
    }
}

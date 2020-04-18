using Orleans.GrainDirectory;
using System;
using System.Collections.Generic;
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
        public override async Task<PlacementResult> OnSelectActivation(PlacementStrategy strategy, GrainId target, IPlacementRuntime context)
        {
            // no need to check if we can find an activation for this client in the cache or local directory partition
            // as TrySelectActivationSynchronously which checks for that should have been called before 
            List<ActivationAddress> addresses;

            // we need to look up the directory entry for this grain on a remote silo
            if (!ClientGrainId.TryParse(target, out var clientId))
            {
                throw new InvalidOperationException($"Unsupported id format: {target}");
            }

            addresses = await context.FullLookup(clientId.GrainId);
            return ChooseRandomActivation(addresses, context);
        }
        
        public override Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, 
            PlacementTarget target, 
            IPlacementContext context)
        {
            throw new InvalidOperationException("Client Observers are not activated using the placement subsystem. Grain " + target.GrainIdentity);
        }
    }
}

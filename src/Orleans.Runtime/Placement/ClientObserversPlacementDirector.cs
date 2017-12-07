using Orleans.GrainDirectory;
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
        public override async Task<PlacementResult> OnSelectActivation(PlacementStrategy strategy, GrainId target, IPlacementRuntime context)
        {
            // no need to check if we can find an activation for this client in the cache or local directory partition
            // as TrySelectActivationSynchronously which checks for that should have been called before 
            AddressesAndTag addresses;

            // we need to look up the directory entry for this grain on a remote silo
            switch (target.Category)
            {
                case UniqueKey.Category.Client:
                    {
                        addresses = await context.FullLookup(target);
                        return ChooseRandomActivation(addresses.Addresses, context);
                    }

                case UniqueKey.Category.GeoClient:
                    {
                        // we need to look up the activations in the remote cluster
                        addresses = await context.LookupInCluster(target, target.Key.ClusterId);
                        return ChooseRandomActivation(addresses.Addresses, context);
                    }

                default:
                    throw new InvalidOperationException("Unsupported client type. Grain " + target);
            }
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

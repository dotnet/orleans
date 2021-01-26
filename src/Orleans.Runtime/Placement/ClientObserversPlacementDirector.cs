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
        public override ValueTask<PlacementResult> OnSelectActivation(PlacementStrategy strategy, GrainId target, IPlacementRuntime context)
        {
            if (!ClientGrainId.TryParse(target, out var clientId))
            {
                throw new InvalidOperationException($"Unsupported id format: {target}");
            }
            
            var grainId = clientId.GrainId;
            
            if (context.FastLookup(grainId, out var address))
            {
                return new ValueTask<PlacementResult>(PlacementResult.IdentifySelection(address));
            }

            return SelectActivationAsync(grainId, context);

            async ValueTask<PlacementResult> SelectActivationAsync(GrainId target, IPlacementRuntime context)
            {
                var address = await context.FullLookup(target);
                if (address is not null)
                {
                    return PlacementResult.IdentifySelection(address);
                }

                return null;
            }
        }
        
        public override Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, 
            PlacementTarget target, 
            IPlacementContext context)
        {
            throw new ClientNotAvailableException(target.GrainIdentity);
        }
    }
}

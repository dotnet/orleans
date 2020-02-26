using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Internal;

namespace Orleans.Runtime.Placement
{
    internal class RandomPlacementDirector : IPlacementDirector, IActivationSelector
    {
        private readonly SafeRandom random = new SafeRandom();

        public virtual async Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementRuntime context)
        {
            var places = (await context.FullLookup(target));
            return ChooseRandomActivation(places, context);
        }

        public bool TrySelectActivationSynchronously(
            PlacementStrategy strategy, GrainId target, IPlacementRuntime context, out PlacementResult placementResult)
        {
            if (context.FastLookup(target, out var addresses))
            {
                placementResult = ChooseRandomActivation(addresses, context);
                return true;
            }

            placementResult = null;
            return false;
        }

        protected PlacementResult ChooseRandomActivation(List<ActivationAddress> places, IPlacementRuntime context)
        {
            if (places.Count <= 0)
            {
                // we return null to indicate that we were unable to select a target from places activations.
                return null;
            }
            if (places.Count == 1)
            {
                return PlacementResult.IdentifySelection(places[0]);
            }
            // places.Count >= 2
            // Choose randomly if there is one, else make a new activation of the target
            // pick local if available (consider making this a purely random assignment of grains).
            var here = context.LocalSilo;
            var local = places.Where(a => a.Silo.Equals(here)).ToList();
            if (local.Count > 0)
                return PlacementResult.IdentifySelection(local[random.Next(local.Count)]);

            return PlacementResult.IdentifySelection(places[random.Next(places.Count)]);
        }

        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var allSilos = context.GetCompatibleSilos(target);
            return Task.FromResult(allSilos[random.Next(allSilos.Count)]);
        }
    }
}

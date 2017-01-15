using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class RandomPlacementDirector : IPlacementDirector<RandomPlacement>
    {
        private readonly SafeRandom random = new SafeRandom();

        public virtual async Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementContext context)
        {
            List<ActivationAddress> places = (await context.Lookup(target)).Addresses;
            return ChooseRandomActivation(places, context);
        }

        protected PlacementResult ChooseRandomActivation(List<ActivationAddress> places, IPlacementContext context)
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

        public virtual Task<PlacementResult> OnAddActivation(
            PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            var grainType = context.GetGrainTypeName(grain);
            var allSilos = context.GetCompatibleSiloList(grain);
            return Task.FromResult(
                PlacementResult.SpecifyCreation(allSilos[random.Next(allSilos.Count)], strategy, grainType));
        }
    }
}

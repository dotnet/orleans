using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans.GrainDirectory;
using Orleans.Placement;

namespace Orleans.Runtime.Placement
{
    internal class SiloServicePlacementDirector : IPlacementDirector, IActivationSelector
    {
        private readonly SafeRandom random = new SafeRandom();

        public virtual Task<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy, GrainId target, IPlacementRuntime runtime)
        {
            return TrySelectActivationSynchronously(strategy, target, runtime, out PlacementResult placementResult)
                ? Task.FromResult(placementResult)
                : Task.FromResult(default(PlacementResult));
        }

        public bool TrySelectActivationSynchronously(
            PlacementStrategy strategy, GrainId target, IPlacementRuntime runtime, out PlacementResult placementResult)
        {
            AddressesAndTag addressesAndTag;
            if (runtime.FastLookup(target, out addressesAndTag) && addressesAndTag.Addresses.Count > 0)
            {
                placementResult = PlacementResult.IdentifySelection(addressesAndTag.Addresses[0]);
                return true;
            }

            placementResult = null;
            return false;
        }

        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            IList<SiloAddress> allSilos = context.GetCompatibleSilos(target);
            if(SiloServicePlacementKeyFormat.TryParsePrimaryKey(allSilos, target.GrainIdentity.PrimaryKeyString, out Tuple<SiloAddress,string> parsedKey))
            {
                return Task.FromResult(parsedKey.Item1);
            }
            throw new OrleansMessageRejectionException($"Silo not available for silo placed grain: {target.GrainIdentity.PrimaryKeyString}");
        }
    }
}

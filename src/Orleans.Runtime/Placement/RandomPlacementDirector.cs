using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class RandomPlacementDirector : IPlacementDirector
    {
        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = context.GetCompatibleSilos(target);

            // If a valid placement hint was specified, use it.
            if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
            {
                return Task.FromResult(placementHint);
            }

            return Task.FromResult(compatibleSilos[Random.Shared.Next(compatibleSilos.Length)]);
        }
    }
}

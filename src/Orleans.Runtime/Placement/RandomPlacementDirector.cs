using System;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class RandomPlacementDirector : IPlacementDirector
    {
        public virtual async Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var compatibleSilos = await context.GetCompatibleSilosAsync(target);

            // If a valid placement hint was specified, use it.
            if (IPlacementDirector.GetPlacementHint(target.RequestContextData, compatibleSilos) is { } placementHint)
            {
                return placementHint;
            }

            return SelectRandomSilo(compatibleSilos);
        }

        protected static SiloAddress SelectRandomSilo(SiloAddress[] compatibleSilos) =>
            compatibleSilos[Random.Shared.Next(compatibleSilos.Length)];
    }
}

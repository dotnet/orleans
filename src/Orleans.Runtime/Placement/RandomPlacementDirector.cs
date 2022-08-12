using System;
using System.Threading.Tasks;
using Orleans.Internal;

namespace Orleans.Runtime.Placement
{
    internal class RandomPlacementDirector : IPlacementDirector
    {
        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var allSilos = context.GetCompatibleSilos(target);
            return Task.FromResult(allSilos[Random.Shared.Next(allSilos.Length)]);
        }
    }
}

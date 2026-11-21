using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class HashBasedPlacementDirector : IPlacementDirector
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

            var sortedSilos = compatibleSilos.OrderBy(s => s).ToArray(); // need to sort the list, so that the outcome is deterministic
            int hash = (int)(target.GrainIdentity.GetUniformHashCode() & 0x7fffffff); // reset highest order bit to avoid negative ints

            return sortedSilos[hash % sortedSilos.Length];
        }
    }
}

using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    internal class HashBasedPlacementDirector : IPlacementDirector
    {
        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var allSilos = context.GetCompatibleSilos(target).OrderBy(s => s).ToArray(); // need to sort the list, so that the outcome is deterministic
            int hash = (int) (target.GrainIdentity.GetUniformHashCode() & 0x7fffffff); // reset highest order bit to avoid negative ints

            return Task.FromResult(allSilos[hash % allSilos.Length]);
        }
    }
}
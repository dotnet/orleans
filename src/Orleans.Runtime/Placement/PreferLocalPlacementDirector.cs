using System.Linq;
using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// PreferLocalPlacementDirector is a single activation placement.
    /// It is similar to RandomPlacementDirector except for how new activations are placed.
    /// When activation is requested (OnSelectActivation), it uses the same algorithm as RandomPlacementDirector to pick one if one already exists.
    /// That is, it checks with the Distributed Directory.
    /// If none exits, it prefers to place a new one in the local silo. If there are no races (only one silo at a time tries to activate this grain),
    /// the local silo wins. In the case of concurrent activations of the first activation of this grain, only one silo wins.
    /// </summary>
    internal class PreferLocalPlacementDirector : RandomPlacementDirector, IPlacementDirector
    {
        private Task<SiloAddress> _cachedLocalSilo;

        public override Task<SiloAddress> 
            OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            // if local silo is not active or does not support this type of grain, revert to random placement
            if (context.LocalSiloStatus != SiloStatus.Active || !context.GetCompatibleSilos(target).Contains(context.LocalSilo))
            {
                return base.OnAddActivation(strategy, target, context);
            }

            return _cachedLocalSilo ??= Task.FromResult(context.LocalSilo);
        }
    }
}

using System.Threading.Tasks;

namespace Orleans.Runtime.Placement
{
    /// <summary>
    /// PreferLocalPlacementDirector is a single activation placement.
    /// It is similar to RandomPlacementDirector except for how new activations are placed.
    /// When activation is requested (OnSelectActivation), it uses the same algorithm as RandomPlacementDirector to pick one if one already exists.
    /// That is, it checks with the Distributed Directory.
    /// If none exits, it prefers to place a new one in the local silo. If there are no races (only one silo at a time tries to activate this grain),
    /// the the local silo wins. In the case of concurrent activations of the first activation of this grain, only one silo wins.
    /// </summary>
    internal class PreferLocalPlacementDirector : RandomPlacementDirector
    {
        internal override Task<PlacementResult> 
            OnAddActivation(PlacementStrategy strategy, GrainId grain, IPlacementContext context)
        {
            var grainType = context.GetGrainTypeName(grain);
            return Task.FromResult( 
                PlacementResult.SpecifyCreation(context.LocalSilo, strategy, grainType));
        }
    }
}

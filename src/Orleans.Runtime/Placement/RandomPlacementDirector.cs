using System.Threading.Tasks;
using Orleans.Internal;

namespace Orleans.Runtime.Placement
{
    internal class RandomPlacementDirector : IPlacementDirector, IActivationSelector
    {
        private readonly SafeRandom random = new SafeRandom();

        public virtual ValueTask<PlacementResult> OnSelectActivation(
            PlacementStrategy strategy,
            GrainId target,
            IPlacementRuntime context)
        {
            if (context.FastLookup(target, out var address))
            {
                return new ValueTask<PlacementResult>(PlacementResult.IdentifySelection(address));
            }

            return SelectActivationAsync(target, context);

            async ValueTask<PlacementResult> SelectActivationAsync(GrainId target, IPlacementRuntime context)
            {
                var address = await context.FullLookup(target);
                if (address is not null)
                {
                    return PlacementResult.IdentifySelection(address);
                }

                return null;
            }
        }

        public virtual Task<SiloAddress> OnAddActivation(
            PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var allSilos = context.GetCompatibleSilos(target);
            return Task.FromResult(allSilos[random.Next(allSilos.Length)]);
        }
    }
}

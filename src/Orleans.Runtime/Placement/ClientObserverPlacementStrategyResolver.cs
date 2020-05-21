using Orleans.Metadata;

namespace Orleans.Runtime.Placement
{
    internal class ClientObserverPlacementStrategyResolver : IPlacementStrategyResolver
    {
        private readonly ClientObserversPlacement _strategy = new ClientObserversPlacement();

        public bool TryResolvePlacementStrategy(GrainType grainType, GrainProperties properties, out PlacementStrategy result)
        {
            if (grainType.IsClient())
            {
                result = _strategy;
                return true;
            }

            result = default;
            return false;
        }
    }
}

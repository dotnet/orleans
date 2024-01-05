using Orleans.Runtime;
using Orleans.Runtime.Placement;

namespace UnitTests.GrainInterfaces
{
    public class TestPlacementStrategyFixedSiloDirector : IPlacementDirector
    {
        public const string TARGET_SILO_INDEX = "TARGET_SILO_INDEX";

        public Task<SiloAddress> OnAddActivation(PlacementStrategy strategy, PlacementTarget target, IPlacementContext context)
        {
            var silos = context.GetCompatibleSilos(target).OrderBy(s => s).ToArray();
            var oddTick = DateTime.UtcNow.Ticks % 2 == 1;

            switch (((TestCustomPlacementStrategy)strategy).Scenario)
            {
                case CustomPlacementScenario.FixedSilo:
                    return Task.FromResult(silos[silos.Length - 2]); // second from last silos.

                case CustomPlacementScenario.ExcludeOne:
                    return Task.FromResult(oddTick ? silos[0] : silos[silos.Length - 1]); // randomly return first or last silos

                case CustomPlacementScenario.RequestContextBased:
                    var index = (int)target.RequestContextData[TARGET_SILO_INDEX];
                    return Task.FromResult(silos[index]);

                default:
                    throw new InvalidOperationException(); // should never get here, only to make compiler happy
            }
        }
    }
}

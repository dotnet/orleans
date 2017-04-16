using Orleans.Placement;

namespace UnitTests.Grains
{
    [HashBasedPlacement(true)]
    public class HashBasedPlacementGrain : CustomPlacementBaseGrain
    {
    }
}

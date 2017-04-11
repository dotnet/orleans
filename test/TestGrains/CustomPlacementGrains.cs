using System.Threading.Tasks;
using Orleans;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
	public abstract class CustomPlacementBaseGrain : Grain, ICustomPlacementTestGrain
	{
		public Task<string> GetRuntimeInstanceId()
		{
			return Task.FromResult(RuntimeIdentity);
		}
	}

	[TestPlacementStrategy(CustomePlacementScenario.FixedSilo)]
	public class CustomPlacement_FixedSiloGrain : CustomPlacementBaseGrain
	{
	}

	[TestPlacementStrategy(CustomePlacementScenario.ExcludeOne)]
	public class CustomPlacement_ExcludeOneGrain : CustomPlacementBaseGrain
	{
	}
}
